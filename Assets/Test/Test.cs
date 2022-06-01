using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Random = UnityEngine.Random;

public class Test : MonoBehaviour
{
    [Serializable]
    public sealed class CameraSettings
    {
        public int X = 128;
        public int Y = 128;
        public int Rate = 30;
        public int Device = 0;
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
        public Vector2Int Octaves = new(4, 8);
        public AudioSource Source;
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
    public ComputeShader Shader;
    public Material Material;
    public CameraSettings Camera = new();
    public MusicSettings Music = new();
    public CursorSettings Cursor = new();
    public TMP_Text Text;

    (bool, bool, bool, bool, bool tab, bool shift, bool space) _buttons;

    IEnumerator Start()
    {
        static RenderTexture Texture(Vector2Int size) => new(size.x, size.y, 1, GraphicsFormat.R32G32B32A32_SFloat)
        {
            enableRandomWrite = true,
            filterMode = FilterMode.Point
        };

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
        var clips = Music.Clips
            .GroupBy(clip => clip.name.Split('_')[0]).Select(group => group.ToArray())
            .ToArray();
        var sources = new Stack<AudioSource>();
        var size = new Vector2Int(device.width, device.height);
        var camera = (input: device, output: Texture(size));
        var color = (input: Texture(size), output: Texture(size));
        var velocity = (input: Texture(size), output: Texture(size));
        var blur = (input: Texture(size), output: Texture(size));
        var emit = (input: Texture(size), output: Texture(size));
        var output = Texture(size);
        var voices = Enumerable.Range(0, size.y).Select(_ => Instantiate(Music.Source)).ToArray();
        var cursor = (color: Cursor.Color, hue: 0f, saturation: 0f, value: 0f,
            sounds: new (AudioClip clip, float volume, float pitch, float pan)[size.y],
            colors: new Color[size.y],
            buffer: new NativeArray<Color>(size.x * size.y, Allocator.Persistent));
        Color.RGBToHSV(Cursor.Color, out cursor.hue, out cursor.saturation, out cursor.value);

        var time = Time.time;
        var request = AsyncGPUReadback.RequestIntoNativeArray(ref cursor.buffer, blur.input);
        var explode = false;
        var next = float.MaxValue;
        for (int y = 0; y < size.y; y++) StartCoroutine(Sound(y));

        while (true)
        {
            yield return null;

            var delta = Time.time - time;
            while (delta > 0 && deltas.Count >= 100) deltas.Dequeue();
            while (delta > 0 && deltas.Count < 100) deltas.Enqueue(1f / delta);
            if (_buttons.tab.Take()) mode = _modes[((int)mode + 1) % _modes.Length];

            var clear = _buttons.Item1.Take();
            if (_buttons.Item2.Take()) { explode = true; next = time + 0.1f; }
            else if (next < time) { explode = true; next = float.MaxValue; }
            else explode = false;

            Text.text = _buttons.shift.Take() || mode > 0 ?
$@"FPS: {deltas.Average():0.00}
Mode: {mode}
Resolution: {size.x} x {size.y}" : "";

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
            Shader.SetFloat("Explode", explode ? Explode : 0f);

            for (int step = 0; step < 10 && time < Time.time && Delta > 0f; step++, time += Delta)
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

            Material.mainTexture = _buttons.space.Take() ? output : mode switch
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

                var instrument = (int)(hue * clips.Length);
                var ratio = (float)y / size.y * (saturation * Music.Saturate + 1f - Music.Saturate);
                var note = Snap((int)Mathf.Lerp(Music.Octaves.x * 12, Music.Octaves.y * 12, ratio), _pentatonic);

                if (clear)
                    cursor.sounds[y].pitch = 0f;
                else if (explode)
                    cursor.sounds[y] = (Music.Clips[random.Next(Music.Clips.Length)], random.NextFloat(), random.NextFloat(0.5f, 2f), random.NextFloat(-1f, 1f));
                else if (value > 0.1f && clips.TryAt(instrument, out var notes) && notes.TryAt(note / 12, out var clip))
                    cursor.sounds[y] = (clip, Mathf.Clamp01(value * Music.Attenuate), Mathf.Pow(2, note % 12 / 12f), pan);
            }
            {
                Color.RGBToHSV(sum, out var hue, out _, out _);
                cursor.hue = Mathf.Lerp(cursor.hue, hue, delta * Cursor.Adapt);
            }

            Array.Sort(voices, (left, right) => right.volume.CompareTo(left.volume));
            for (int i = 0; i < voices.Length && i < Music.Voices; i++)
                if (voices[i] is AudioSource source && !source.isPlaying) source.Play();
            for (int i = Music.Voices; i < voices.Length; i++)
                if (voices[i] is AudioSource source && source.isPlaying) voices[i].Stop();
        }

        IEnumerator Sound(int y)
        {
            var source = voices[y];
            while (source)
            {
                if (explode && next < float.MaxValue) source.Stop();
                var (clip, volume, pitch, pan) = cursor.sounds[y];

                if (clip == null) Interpolate(source, 0f, pitch, pan, Music.Fade);
                else if (source.clip == clip) Interpolate(source, volume, pitch, pan, Music.Fade);
                else if (source.volume < 0.1f)
                {
                    source.Stop();
                    source.name = clip.name;
                    source.clip = clip;
                    source.volume = volume * 2.5f;
                    source.pitch = pitch;
                    source.panStereo = pan;
                    source.time = 0f;
                }
                else Interpolate(source, 0f, pitch, pan, Music.Fade);
                yield return null;
            }

            static void Interpolate(AudioSource source, float volume, float pitch, float pan, float fade)
            {
                source.volume = Mathf.Lerp(source.volume, volume, Time.deltaTime * fade);
                source.pitch = Mathf.Lerp(source.pitch, pitch, Time.deltaTime * 5f);
                source.panStereo = Mathf.Lerp(source.panStereo, pan, Time.deltaTime * 5f);
            }
        }
    }

    void Update()
    {
        _buttons.Item1 |= Input.GetKey(KeyCode.Alpha1) || Input.GetKey(KeyCode.Keypad1);
        _buttons.Item2 |= Input.GetKeyDown(KeyCode.Alpha2) || Input.GetKeyDown(KeyCode.Keypad2);
        _buttons.Item3 |= Input.GetKey(KeyCode.Alpha3) || Input.GetKey(KeyCode.Keypad3);
        _buttons.Item4 |= Input.GetKey(KeyCode.Alpha4) || Input.GetKey(KeyCode.Keypad4);
        _buttons.tab |= Input.GetKeyDown(KeyCode.Tab);
        _buttons.shift |= Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        _buttons.space |= Input.GetKey(KeyCode.Space);
    }
}
