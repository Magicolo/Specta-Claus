using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TMPro;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.UI;
using Random = UnityEngine.Random;

public sealed class Main : MonoBehaviour
{
    [Serializable]
    public sealed class CameraSettings
    {
        public int X = 128;
        public int Y = 128;
        public int Rate = 30;
        public int Device = 0;
        public Image Output;
        public Image Flash;
        public Camera Camera;
    }

    [Serializable]
    public sealed class CursorSettings
    {
        [ColorUsage(true, true)]
        public Color Color = Color.yellow;
        [Range(0f, 10f)]
        public float Adapt = 1f;
    }

    [Serializable]
    public sealed class MusicSettings
    {
        public int Tempo = 120;
        public int Beats = 16;
        public int Voices = 16;
        [Range(0f, 1f)]
        public float Saturate = 0.1f;
        [Range(0f, 10f)]
        public float Fade = 2.5f;
        [Range(0f, 1f)]
        public float Attenuate = 0.25f;
        [Range(0f, 250f)]
        public float Loud = 25f;
        public Vector2Int Octaves = new(4, 8);
        public AudioSource Particle;
        public AudioSource Clear;
        public AudioSource Save;
        public AudioSource Load;
        public InstrumentSettings[] Instruments;
    }

    [Serializable]
    public sealed class InstrumentSettings
    {
        public Color Color = Color.red;
        public Vector2Int Octaves = new(2, 8);
        public float Volume = 1f;
        public AudioClip[] Clips;
    }

    enum Modes
    {
        None = 0,
        Color = 1,
        Velocity = 2,
        Camera = 3,
        Blur = 4,
        Emit = 5,
    }

    static readonly int[] _pentatonic = { 0, 3, 5, 7, 10 };
    static readonly Modes[] _modes = (Modes[])typeof(Modes).GetEnumValues();

    static int Snap(int note, int[] notes)
    {
        var source = note % 12;
        var target = notes.OrderBy(note => Math.Abs(note - source)).FirstOrDefault();
        return note - source + target;
    }

    [Range(0f, 1f)]
    public float Delta = 0.01f;
    public float Explode = 1f;
    public int Snapshots = 3;
    public ComputeShader Shader;
    public CameraSettings Camera = new();
    public MusicSettings Music = new();
    public CursorSettings Cursor = new();
    public TMP_Text Text;

    (bool, bool, bool, bool, bool tab, bool shift, bool space, bool left, bool right, bool up, bool down) _buttons;

    IEnumerator Start()
    {
        static RenderTexture Render(Vector2Int size) => new(size.x, size.y, 1, GraphicsFormat.R32G32B32A32_SFloat)
        {
            enableRandomWrite = true,
            filterMode = FilterMode.Point
        };

        static Texture2D Texture(Vector2Int size) => new(size.x, size.y, GraphicsFormat.R32G32B32A32_SFloat, TextureCreationFlags.None)
        {
            alphaIsTransparency = true,
            filterMode = FilterMode.Point,
        };

        static NativeArray<Color> Buffer(int count)
        {
            var buffer = new NativeArray<Color>(count, Allocator.Persistent);
            Application.quitting += () => { AsyncGPUReadback.WaitAllRequests(); buffer.Dispose(); };
            return buffer;
        }

        Debug.Log($"Devices: {string.Join(", ", WebCamTexture.devices.Select(device => device.name))}");
        var device = new WebCamTexture(WebCamTexture.devices[Camera.Device].name, Camera.X, Camera.Y, Camera.Rate)
        {
            autoFocusPoint = null,
            filterMode = FilterMode.Point
        };
        device.Play();
        while (device.width < 32 && device.height < 32) yield return null;
        Debug.Log($"Camera: {device.deviceName} | Resolution: {device.width}x{device.height} | FPS: {device.requestedFPS} | Graphics: {device.graphicsFormat}");

        var mode = Modes.None;
        var random = new System.Random();
        var deltas = new Queue<float>();
        var sources = new Stack<AudioSource>();
        var size = new Vector2Int(device.width, device.height);
        var camera = (input: device, output: Render(size));
        var color = (input: Render(size), output: Render(size));
        var velocity = (input: Render(size), output: Render(size));
        var blur = (input: Render(size), output: Render(size));
        var emit = (input: Render(size), output: Render(size));
        var output = Render(size);
        var voices = Enumerable.Range(0, size.y).Select(_ => Instantiate(Music.Particle)).ToArray();
        var cursor = (color: Cursor.Color, hue: 0f, saturation: 0f, value: 0f,
            output: Render(size),
            sounds: new (AudioClip clip, float volume, float pitch, float pan)[size.y],
            colors: new Color[size.y],
            buffer: Buffer(size.x * size.y));
        var snapshots = (
            index: 0,
            magnitudes: new float[size.x * size.y],
            pending: Texture(size),
            buffer: Buffer(size.x * size.y),
            archive: Enumerable.Range(0, Snapshots).Select(_ => (texture: Texture(size), score: 0f)).ToArray());
        var directory = Path.Combine(Application.dataPath, "..", "Snapshots");
        if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
        Color.RGBToHSV(Cursor.Color, out cursor.hue, out cursor.saturation, out cursor.value);
        Archive();
        Debug.Log($"Archive: {snapshots.index} | {string.Join(", ", snapshots.archive.Take(snapshots.index).Select(pair => pair.score))}");


        var time = Time.time;
        var request = AsyncGPUReadback.RequestIntoNativeArray(ref cursor.buffer, blur.input);
        var saving = 0f;
        var loading = (set: new HashSet<int>(), last: new Queue<int>());
        var exploding = false;
        var next = float.MaxValue;
        var attenuate = 1f;
        for (int y = 0; y < size.y; y++) StartCoroutine(Sound(y));

        while (true)
        {
            yield return null;
            UnityEngine.Cursor.visible = Application.isEditor;

            var delta = Time.time - time;
            while (delta > 0 && deltas.Count >= 100) deltas.Dequeue();
            while (delta > 0 && deltas.Count < 100) deltas.Enqueue(1f / delta);
            if (_buttons.tab.Take()) mode = _modes[((int)mode + 1) % _modes.Length];
            Text.text = _buttons.shift.Take() || mode > 0 ?
$@"FPS: {deltas.Average():0.00}
Mode: {mode}
Resolution: {size.x} x {size.y}" : "";

            attenuate = Mathf.Clamp01(2f - voices.Sum(voice => voice.isPlaying ? voice.volume : 0f) / Music.Loud);
            var clear = _buttons.Item1.Take();
            var explode = _buttons.Item2.Take();
            var save = _buttons.Item3.Take();
            var load = _buttons.Item4.Take();
            Music.Clear.volume = Mathf.Clamp01(1f - Music.Clear.time);
            Music.Save.volume = Mathf.Clamp01(1f - Music.Save.time);
            Music.Load.volume = Mathf.Clamp01(1f - Music.Load.time);
            Camera.Flash.material.SetFloat("_Flash", Mathf.Lerp(Camera.Flash.material.GetFloat("_Flash"), 0f, Time.deltaTime * 5f));

            if (explode) { exploding = true; next = time + 0.1f; }
            else if (next < time) { exploding = true; next = float.MaxValue; }
            else exploding = false;

            if (clear || explode || save || load) Camera.Flash.material.SetFloat("_Flash", 10f);
            if (clear)
            {
                Music.Clear.Play();
                Music.Clear.volume = 1f;
            }
            if (save)
            {
                Music.Save.Play();
                Music.Save.volume = 1f;
                Music.Save.pitch = 0.5f;
                foreach (var item in Save()) yield return item;
                Music.Save.Play();
                Music.Save.pitch = 1f;
            }
            if (load)
            {
                Music.Load.Play();
                Music.Load.volume = 1f;
                Load();
            }

            var toColumn = (double)size.x / Music.Beats;
            var cursorBeat = time / 60f * Music.Tempo % Music.Beats;
            var cursorColumn = (int)(cursorBeat * toColumn);
            cursor.color = Color.HSVToRGB(cursor.hue, cursor.saturation, cursor.value);

            Shader.SetInt("Width", size.x);
            Shader.SetInt("Height", size.y);
            Shader.SetTexture(0, "Output", output);
            Shader.SetTexture(0, "CameraInput", camera.input);
            Shader.SetTexture(0, "CameraOutput", camera.output);
            Shader.SetInt("CursorColumn", cursorColumn);
            Shader.SetVector("CursorColor", cursor.color);
            Shader.SetFloat("Time", time);
            Shader.SetFloat("Delta", Delta);
            Shader.SetBool("Clear", clear);
            Shader.SetFloat("Explode", exploding ? Explode : 0f);

            var steps = Math.Clamp((int)(Time.time - time) / Delta, 1, 10);
            for (int step = 0; step < steps; step++, time += Delta)
            {
                Shader.SetFloat("Time", time);
                Shader.SetVector("Seed", new Vector4(Random.value, Random.value, Random.value, Random.value));
                Shader.SetTexture(0, "VelocityInput", velocity.input);
                Shader.SetTexture(0, "VelocityOutput", velocity.output);
                Shader.SetTexture(0, "ColorInput", color.input);
                Shader.SetTexture(0, "ColorOutput", color.output);
                Shader.SetTexture(0, "BlurInput", blur.input);
                Shader.SetTexture(0, "BlurOutput", blur.output);
                Shader.SetTexture(0, "EmitInput", emit.input);
                Shader.SetTexture(0, "EmitOutput", emit.output);
                Shader.Dispatch(0, size.x / 8, size.y / 4, 1);
                (velocity.input, velocity.output) = (velocity.output, velocity.input);
                (color.input, color.output) = (color.output, color.input);
                (blur.input, blur.output) = (blur.output, blur.input);
                (emit.input, emit.output) = (emit.output, emit.input);
            }
            while (time < Time.time) time += Delta;

            Camera.Output.material.mainTexture = _buttons.space.Take() ? output : mode switch
            {
                Modes.None => output,
                Modes.Color => color.output,
                Modes.Velocity => velocity.output,
                Modes.Camera => device,
                Modes.Blur => blur.output,
                Modes.Emit => emit.output,
                _ => default,
            };

            if (request.done)
            {
                for (int y = 0; y < size.y; y++) cursor.colors[y] = cursor.buffer[cursorColumn + y * size.x];
                request = AsyncGPUReadback.RequestIntoNativeArray(ref cursor.buffer, blur.input);
            }

            var sum = cursor.color;
            var pan = Mathf.Clamp01((float)cursorColumn / size.x) * 2f - 1f;
            for (int y = 0; y < size.y; y++)
            {
                var pixel = cursor.colors[y];
                sum += pixel;
                Color.RGBToHSV(pixel, out var hue, out var saturation, out var value);

                var instrument = Music.Instruments.MinBy(instrument => Vector3.Distance((Vector4)instrument.Color, (Vector4)pixel));
                var ratio = (float)y / size.y * (saturation * Music.Saturate + 1f - Music.Saturate);
                var note = Snap((int)Mathf.Lerp(Music.Octaves.x * 12, Music.Octaves.y * 12, ratio), _pentatonic);

                if (clear)
                    cursor.sounds[y].pitch = 0f;
                else if (exploding && Music.Instruments.TryRandom(out instrument) && instrument.Clips.TryRandom(out var clip))
                    cursor.sounds[y] = (clip, random.NextFloat(), random.NextFloat(0.5f, 2f), random.NextFloat(-1f, 1f));
                else if (value > 0f && instrument.Clips.TryAt(Math.Clamp(note / 12, instrument.Octaves.x, instrument.Octaves.y), out clip))
                    cursor.sounds[y] = (clip, Mathf.Clamp01(Mathf.Pow(value, 0.75f) * Music.Attenuate), Mathf.Pow(2, note % 12 / 12f), pan);
                else
                {
                    cursor.sounds[y].volume = 0f;
                    cursor.sounds[y].clip = default;
                }
            }
            {
                Color.RGBToHSV(sum, out var hue, out _, out _);
                cursor.hue = Mathf.Lerp(cursor.hue, hue, delta * Cursor.Adapt);
            }

            if (exploding)
            {
                for (int i = 0; i < voices.Length; i++)
                    if (voices[i] is AudioSource source && source.isPlaying) source.Stop();
            }
            else
            {
                Array.Sort(voices, (left, right) => right.volume.CompareTo(left.volume));
                for (int i = 0; i < voices.Length && i < Music.Voices; i++)
                    if (voices[i] is AudioSource source && !source.isPlaying) source.Play();
                for (int i = Music.Voices; i < voices.Length; i++)
                    if (voices[i] is AudioSource source && source.isPlaying) source.Stop();
            }
        }

        IEnumerator Sound(int y)
        {
            var source = voices[y];
            var last = (clip: default(AudioClip), time: 0f);
            while (source)
            {
                var (clip, volume, pitch, pan) = cursor.sounds[y];
                if (clip == null || last.clip == clip)
                {
                    source.volume = Mathf.Lerp(source.volume, volume, Time.deltaTime * Music.Fade);
                    source.pitch = Mathf.Lerp(source.pitch, pitch, Time.deltaTime * 5f);
                    source.panStereo = Mathf.Lerp(source.panStereo, pan, Time.deltaTime * 5f);
                    last.clip = clip;
                }
                else
                {
                    source.Stop();
                    source.name = clip.name;
                    source.clip = clip;
                    source.volume = volume * attenuate;
                    source.pitch = pitch;
                    source.panStereo = pan;
                    source.time = 0f;
                    last = (clip, time);
                }
                yield return null;
            }
        }

        IEnumerable Save()
        {
            if (time - saving >= 2.5f)
            {
                var request = AsyncGPUReadback.RequestIntoNativeArray(ref snapshots.buffer, blur.input);
                while (!request.done) yield return null;

                for (int i = 0; i < snapshots.buffer.Length; i++)
                {
                    var color = (Vector3)(Vector4)snapshots.buffer[i];
                    snapshots.magnitudes[i] = color.magnitude;
                }

                Array.Sort(snapshots.magnitudes);
                var average = snapshots.magnitudes.Average();
                var bright = snapshots.magnitudes.Skip(snapshots.magnitudes.Length * 3 / 4).Average();
                var dark = snapshots.magnitudes.Take(snapshots.magnitudes.Length / 2).Average();
                var score =
                    Math.Min(bright, 7.5f) + // Reward if there are bright pixels.
                    Math.Max(7.5f - dark, 0f) + // Reward if there are dark pixels.
                    Math.Max(7.5f - Math.Abs(average - 1f), 0) + // Reward if global average is close to 1.
                    Math.Clamp(average - 1f, -1f, 0f) * 15f + // Penalize if global average is lower than 1.
                    Math.Clamp(9f - average, -1f, 0f) * 15f; // Penalize if global average is higher than 9.

                if (score >= 5f)
                {
                    var file = Path.Combine(directory, $"{score}~{DateTime.Now.ToFileTime()}".Replace('.', '_'));
                    var snapshot = $"{file}.snapshot";
                    var png = $"{file}.png";
                    Camera.Flash.enabled = false;
                    Camera.Output.material.mainTexture = emit.input;
                    Camera.Camera.Render();
                    ScreenCapture.CaptureScreenshot(png, 4);
                    TryWrite(snapshot, snapshots.buffer);
                    while (!File.Exists(png)) yield return null;

                    Camera.Flash.enabled = true;
                    snapshots.pending.SetPixelData(snapshots.buffer, 0);
                    Commit(score);
                    Sort();
                    saving = time;
                    Debug.Log($"Save: {score} | {file}");
                }
                yield return null;
            }
        }

        void Load()
        {
            var count = Math.Min(snapshots.index, snapshots.archive.Length);
            var index = (int)(Math.Pow(random.NextDouble(), 2.5) * Math.Min(snapshots.index, snapshots.archive.Length));
            while (loading.last.Count > count / 10 && loading.last.TryDequeue(out var last)) loading.set.Remove(last);
            for (; index < count; index++) if (loading.set.Add(index)) { loading.last.Enqueue(index); break; }

            var source = snapshots.archive[index];
            var previous = RenderTexture.active;
            RenderTexture.active = blur.input;
            Graphics.CopyTexture(source.texture, blur.input);
            RenderTexture.active = previous;
            Debug.Log($"Load: {index} / {count}");
        }

        void Commit(float score)
        {
            var index = Math.Min(snapshots.index++, snapshots.archive.Length - 1);
            snapshots.pending.Apply();
            var snapshot = snapshots.archive[index].Set((texture: snapshots.pending, score));
            snapshots.pending = snapshot.texture;
        }

        void Sort() => Array.Sort(snapshots.archive, (left, right) => right.score.CompareTo(left.score));

        void Archive()
        {
            var pairs = Directory.EnumerateFiles(directory, "*.snapshot")
                .AsParallel()
                .Select(path =>
                    float.TryParse(Path.GetFileNameWithoutExtension(path).Split('~')[0].Replace('_', '.'), out var score) ?
                    (path, score) : default)
                .Where(pair => pair.path is not null && pair.score > 0)
                .OrderByDescending(pair => pair.score)
                .Take(snapshots.archive.Length)
                .Select(pair => TryRead(pair.path, out var pixels) ? (pixels, pair.score) : default)
                .Where(pair => pair.pixels is not null && pair.score > 0);
            foreach (var (pixels, score) in pairs)
            {
                snapshots.pending.SetPixels(pixels);
                Commit(score);
            }
            Sort();
        }

        static bool TryRead(string path, out Color[] pixels)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
                using var reader = new BinaryReader(stream);
                if (reader.ReadInt32() == 1) // Version
                {
                    pixels = new Color[reader.ReadInt32()];
                    for (int i = 0; i < pixels.Length; i++)
                        pixels[i] = new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                    return true;
                }
            }
            catch { }

            pixels = default;
            return false;
        }

        static bool TryWrite(string path, NativeArray<Color> pixels)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
                using var writer = new BinaryWriter(stream);
                writer.Write(1); // Version
                writer.Write(pixels.Length);
                foreach (var color in pixels)
                {
                    writer.Write(color.r);
                    writer.Write(color.g);
                    writer.Write(color.b);
                    writer.Write(color.a);
                }
                return true;
            }
            catch { return false; }
        }
    }

    void Update()
    {
        _buttons.Item1 |= Input.GetKeyDown(KeyCode.Alpha1) || Input.GetKeyDown(KeyCode.Keypad1);
        _buttons.Item2 |= Input.GetKeyDown(KeyCode.Alpha2) || Input.GetKeyDown(KeyCode.Keypad2);
        _buttons.Item3 |= Input.GetKeyDown(KeyCode.Alpha3) || Input.GetKeyDown(KeyCode.Keypad3);
        _buttons.Item4 |= Input.GetKeyDown(KeyCode.Alpha4) || Input.GetKeyDown(KeyCode.Keypad4);
        _buttons.left |= Input.GetKeyDown(KeyCode.LeftArrow);
        _buttons.right |= Input.GetKeyDown(KeyCode.RightArrow);
        _buttons.up |= Input.GetKeyDown(KeyCode.UpArrow);
        _buttons.down |= Input.GetKeyDown(KeyCode.DownArrow);
        _buttons.tab |= Input.GetKeyDown(KeyCode.Tab);
        _buttons.shift |= Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        _buttons.space |= Input.GetKey(KeyCode.Space);
    }
}
