using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;
using Debug = UnityEngine.Debug;
using Random = System.Random;

public class Main : MonoBehaviour
{
    [Serializable]
    public sealed class PolarizeSettings
    {
        [Range(0f, 1f)]
        public float Pre;
        [Range(0f, 1f)]
        public float Post;
    }

    [Serializable]
    public sealed class MultiplySettings
    {
        [Range(0f, 3f)]
        public float Pre = 1f;
        [Range(0f, 3f)]
        public float Post = 1f;
    }

    [Serializable]
    public sealed class CameraSettings
    {
        public int X = 128;
        public int Y = 128;
        public int Rate = 30;
        public int Device = 0;
        public MultiplySettings Multiply = new();
        public float Threshold = 0.5f;
        public float Contrast = 5;
        public float Jitter = 3f;
        [Range(0f, 1f)]
        public float Shape = 0.5f;
        [Range(-1f, 1f)]
        public float Inside = 0f;
        [Range(-1f, 1f)]
        public float Border = 1f;
        [Range(0f, 1f)]
        public float Unite = 0.5f;
        public PolarizeSettings Polarize = new();
        public Camera Camera;
    }

    [Serializable]
    public sealed class ParticleSettings
    {
        public float Fade = 5f;
        public float Friction = 5f;
        public float Power = 5f;
        [Range(0f, 1f)]
        public float Forward = 0.75f;
        [Range(0f, 1f)]
        public float Shine = 0.75f;
        [Range(0f, 1f)]
        public float Shift = 0.25f;
        [Range(0f, 1f)]
        public float Polarize = new();
        public Vector2 Speed = new(5f, 10f);
        public Vector2 Count = new(5f, 25f);
        public Vector2 Radius = new(1f, 3f);
    }

    [Serializable]
    public sealed class MusicSettings
    {
        public int Tempo = 120;
        public int Beats = 16;
        public int Voices = 16;
        public float Threshold = 0.1f;
        public float Duration = 0.1f;
        public float Fade = 0.1f;
        public Vector2Int Octaves = new(4, 8);
        public AudioSource Source;
        public AudioClip[] Clips;
    }

    [Serializable]
    public sealed class CursorSettings
    {
        [ColorUsage(true, true)]
        public Color Color;
        public float Speed = 0.25f;
        [Range(0f, 1f)]
        public float Blend = 0.25f;
        [Range(0f, 100f)]
        public float Trail = 100f;
        [Range(0f, 10f)]
        public float Fade = 5f;
        [Range(0f, 10f)]
        public float Adapt = 1f;
    }

    sealed class Sound
    {
        public AudioClip Clip;
        public float Volume;
        public float Pitch;
        public float Pan;
    }

    sealed class Shape
    {
        public Color Color;
        public int Pixels;
    }

    struct Datum
    {
        public Color Last;
        public (Color color, Vector2 velocity, TimeSpan delta) Particle;
        public (Color color, bool valid) Read;
        public (Color color, Color add, Shape shape) Shape;
    }

    static readonly int[] _pentatonic = { 0, 3, 5, 7, 10 };

    [StructLayout(LayoutKind.Sequential)]
    public struct ARGB
    {
        public byte A;
        public byte R;
        public byte G;
        public byte B;

        public static implicit operator Color(ARGB color) => (Color32)color;
        public static implicit operator Color32(ARGB color) => new(color.R, color.G, color.B, color.A);
        public static implicit operator ARGB(Color32 color) => new() { A = color.a, R = color.r, G = color.g, B = color.b };
        public static implicit operator ARGB(Color color) => (Color32)color;
    }

    public ParticleSettings Particle = new();
    public CameraSettings Camera = new();
    public CursorSettings Cursor = new();
    public MusicSettings Music = new();

    public Image Renderer;
    public Text FPS;

    bool _fps;
    bool _calibrate;
    AudioClip[][] _clips = { };
    readonly Stack<AudioSource> _sources = new();

    void Awake() => _clips = Music.Clips
        .GroupBy(clip => clip.name.Split('_')[0]).Select(group => group.ToArray())
        .ToArray();

    IEnumerator Start()
    {
        Debug.Log($"Devices: {string.Join(", ", WebCamTexture.devices.Select(device => device.name))}");
        var device = WebCamTexture.devices[Camera.Device];
        var texture = new WebCamTexture(device.name, Camera.X, Camera.Y, Camera.Rate) { autoFocusPoint = null, filterMode = FilterMode.Point };
        texture.Play();
        while (texture.width < 32 && texture.height < 32) yield return null;
        Debug.Log($"Camera: {texture.deviceName} | Resolution: {texture.width}x{texture.height} | FPS: {texture.requestedFPS} | Graphics: {texture.graphicsFormat}");

        var (width, height) = (texture.width, texture.height);
        var random = new Random();
        var pixels = new Color[width * height];
        var cursor = Cursor.Color;
        var camera = (
            texture,
            buffer: new NativeArray<ARGB>(width * height, Allocator.Persistent),
            read: new ARGB[pixels.Length],
            write: new ARGB[pixels.Length]);
        var fps = (deltas: default(TimeSpan[]), index: 0);
        var data = new Datum[pixels.Length];
        var sounds = (add: new List<Sound>(), play: new List<Sound>());
        var render = Camera.Camera.targetTexture;
        Renderer.sprite = Sprite.Create(
            new Texture2D(width, height, TextureFormat.RGBAFloat, 0, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Point },
            new(0, 0, width, height),
            new(width / 2, height / 2), 1);

        // Wait until fps has stabilized.
        for (int i = 0; i < 10; i++) yield return null;
        var watch = Stopwatch.StartNew();
        var time = watch.Elapsed;
        while (true)
        {
            UnityEngine.Cursor.visible = Application.isEditor;

            var delta = TimeSpan.FromSeconds(Math.Max(60.0 / Music.Tempo * Music.Beats / width, 1.0 / Camera.Rate));
            var current = watch.Elapsed;
            UpdateFPS(current - time);

            if (_calibrate)
            {
                Camera.Camera.targetTexture = null;
                var request = AsyncGPUReadback.RequestIntoNativeArray(ref camera.buffer, texture);
                while (!request.done) yield return null;
                for (int i = 0; i < camera.buffer.Length; i++) pixels[i] = camera.buffer[i];
                Renderer.sprite.texture.SetPixels(pixels);
                Renderer.sprite.texture.Apply(false, false);
                yield return null;
            }
            else
            {
                Camera.Camera.targetTexture = render;

                for (int i = 0; i < 10 && current - time >= delta; i++, time += delta)
                {
                    var process = Task.Run(() =>
                    {
                        var toBeat = (double)Music.Beats / width;
                        var toColumn = (double)width / Music.Beats;
                        var cursorBeat = time.TotalMinutes * Music.Tempo % Music.Beats;
                        var cursorColumn = cursorBeat * toColumn;
                        var friction = 1f - Mathf.Clamp01(Particle.Friction * (float)delta.TotalSeconds);
                        var fade = 1f - Mathf.Clamp01(Particle.Fade * (float)delta.TotalSeconds);
                        var sum = cursor;

                        // Pass 1
                        for (int i = 0; i < data.Length; i++)
                            UpdateParticle(ref data[i], i % width, i / width, friction, fade, delta);

                        // Pass 2
                        for (int i = 0; i < data.Length; i++)
                        {
                            ref var datum = ref data[i];
                            var x = i % width;
                            var y = i / width;
                            var wrap = x > cursorColumn ? x - width : x;
                            var bar = (float)Math.Pow(1.0 - Math.Clamp(cursorColumn - wrap, 0, Cursor.Trail) / Cursor.Trail, Cursor.Fade) * cursor;

                            UpdateRead(ref datum, camera.read[i]);
                            UpdateShape(ref datum, x, y);
                            UpdateBlur(ref datum, delta);
                            sum += UpdatePixel(datum, ref pixels[i], x, y, (int)cursorColumn);
                        }

                        Color.RGBToHSV(sum, out var hue, out var saturation, out _);
                        cursor = cursor.MapHSV(color => (
                            Mathf.Lerp(color.h, hue, (float)delta.TotalSeconds * Cursor.Adapt),
                            Mathf.Lerp(color.s, saturation, (float)delta.TotalSeconds * Cursor.Adapt),
                            color.v)).Finite().Clamp(0f, 5f);
                    });

                    var request = AsyncGPUReadback.RequestIntoNativeArray(ref camera.buffer, texture);

                    // Play sounds.
                    for (int j = 0; j < Music.Voices && j < sounds.play.Count; j++)
                        StartCoroutine(Play(sounds.play[UnityEngine.Random.Range(0, sounds.play.Count)]));
                    sounds.play.Clear();

                    while (!request.done) yield return null;
                    camera.buffer.CopyTo(camera.write);
                    while (!process.IsCompleted) yield return null;
                    if (process.IsFaulted) Debug.LogException(process.Exception);
                    // The swaps must be done after 'process' is complete since it uses both the 'read' and 'last' buffers.
                    (camera.write, camera.read) = (camera.read, camera.write);
                    (sounds.add, sounds.play) = (sounds.play, sounds.add);

                    Renderer.sprite.texture.SetPixels(pixels);
                    Renderer.sprite.texture.Apply(false, false);
                    yield return null;
                }
                // If 'time' is more than '10' frames late, skip the additional frames to prevent a lag spiral.
                while (current - time >= delta) { time += delta; Debug.Log("Skipped a frame."); }
            }
        }

        void UpdateParticle(ref Datum datum, int x, int y, float friction, float fade, TimeSpan delta)
        {
            ref var particle = ref datum.Particle;
            particle.velocity *= friction;
            particle.color *= fade;

            var velocity = particle.velocity * (float)particle.delta.TotalSeconds;
            if (Mathf.Abs(velocity.x) < 1f && Mathf.Abs(velocity.y) < 1f)
            {
                if (particle.velocity.sqrMagnitude > 0.01f)
                    particle.delta += delta;
                else
                    particle.delta = default;
            }
            else
            {
                var position = (x: (int)(x + velocity.x).Wrap(width), y: (int)(y + velocity.y).Wrap(height));
                ref var target = ref data[position.x + position.y * width].Particle;
                target.velocity += particle.velocity.Take();
                target.color = Color.Lerp(particle.color, target.color, 0.5f);
                particle.delta = default;
            }
        }

        Color UpdatePixel(in Datum datum, ref Color pixel, int x, int y, int column)
        {
            var wrap = x > column ? x - width : x;
            var bar = (float)Math.Pow(1.0 - Math.Clamp(column - wrap, 0, Cursor.Trail) / Cursor.Trail, Cursor.Fade) * cursor;
            var valid = datum.Read.valid;
            var shape = datum.Shape.color;
            var last = datum.Last;
            var particle = datum.Particle.color;
            pixel = (bar + last + particle).Finite();

            if (valid && x == column)
            {
                var ratio = Mathf.Pow(Math.Max(Math.Max(shape.r, shape.g), shape.b), Particle.Power);
                var radius = Mathf.Lerp(Particle.Radius.x, Particle.Radius.y, ratio);
                var count = Mathf.RoundToInt(Mathf.Lerp(Particle.Count.x, Particle.Count.y, ratio));
                EmitParticle(x, y, Color.Lerp(shape, cursor, Cursor.Blend), radius, count);
                EmitSound(x, y, shape);
                return shape;
            }
            else return default;
        }

        void UpdateFPS(TimeSpan delta)
        {
            if (FPS.enabled = _fps)
            {
                fps.deltas ??= new TimeSpan[256];
                fps.deltas[fps.index++ % fps.deltas.Length] = delta;
                FPS.text = $"FPS: {fps.deltas.Take(fps.index).Average(delta => 1.0 / delta.TotalSeconds):00.000}";
            }
            else if (fps.index > 0 && fps.deltas is not null)
            {
                fps.index = 0;
                fps.deltas = null;
            }
        }

        void EmitParticle(int x, int y, Color color, float radius, int count)
        {
            var shift = color.ShiftHue(Particle.Shift);
            for (int i = 0; i < count; i++)
            {
                var direction = new Vector2(random.NextFloat(-radius, radius * Particle.Forward), random.NextFloat(-radius, radius));
                var position = (x: (int)(x + direction.x).Wrap(width), y: (int)(y + direction.y).Wrap(height));
                ref var particle = ref data[position.x + position.y * width].Particle;
                particle = (
                    Color.Lerp(particle.color, particle.color.Polarize(Particle.Polarize) + shift, Particle.Shine),
                    particle.velocity + direction * random.NextFloat(Particle.Speed.x, Particle.Speed.y),
                    particle.delta);
            }
        }

        IEnumerator Play(Sound sound)
        {
            var source = _sources.TryPop(out var value) && value ? value : Instantiate(Music.Source);
            source.name = sound.Clip.name;
            source.clip = sound.Clip;
            source.volume = sound.Volume;
            source.pitch = sound.Pitch;
            source.panStereo = sound.Pan;
            source.Play();

            for (var counter = 0f; counter < Music.Duration && source.isPlaying; counter += Time.deltaTime)
                yield return null;

            for (var counter = 0f; counter < Music.Fade && source.isPlaying; counter += Time.deltaTime)
            {
                source.volume = sound.Volume * (1f - Mathf.Clamp01(counter / Music.Fade));
                yield return null;
            }

            source.Stop();
            _sources.Push(source);
        }

        void EmitSound(int x, int y, Color color)
        {
            Color.RGBToHSV(color, out var hue, out var saturation, out var value);
            if (value < Music.Threshold) return;

            var note = Snap((int)((float)y / height * 80f), _pentatonic);
            if (_clips.TryAt((int)(hue * _clips.Length), out var clips) && clips.TryAt(note / 12, out var clip))
                sounds.add.Add(new Sound
                {
                    Clip = clip,
                    Volume = value * value,
                    Pitch = Mathf.Pow(2, note % 12 / 12f),
                    Pan = Mathf.Clamp01((float)x / width) * 2f - 1f,
                });
        }

        ref readonly Datum Get(int x, int y) => ref data[x + y * width];

        void UpdateShape(ref Datum datum, int x, int y)
        {
            ref var shape = ref datum.Shape;
            var read = datum.Read.color;
            var left = Math.Max(x - 1, 0);
            var right = Math.Min(x + 1, width - 1);
            var bottom = Math.Max(y - 1, 0);
            var top = Math.Min(y + 1, height - 1);
            if (shape.shape == null)
            {
                if (Valid(read, Camera.Shape))
                {
                    // Try to share a shape in the surrounding pixels.
                    for (int xx = left; xx <= right; xx++)
                        for (int yy = bottom; yy <= top; yy++)
                            if (Get(xx, yy).Shape.shape is Shape other)
                            {
                                other.Pixels++;
                                other.Color += read;
                                shape = (default, read, other);
                                return;
                            }

                    // No shape was found around this pixel so create one.
                    shape = (default, read, new Shape { Color = read, Pixels = 1 });
                }
            }
            else if (Valid(read, Camera.Shape))
            {
                // Pixel is still valid in its shape.
                shape.shape.Color -= shape.add;
                shape.shape.Color += shape.add = read;
                // Determine if the pixel is on the border of the shape.
                for (int xx = left; xx <= right; xx++)
                    for (int yy = bottom; yy <= top; yy++)
                        if (Get(xx, yy).Shape.shape != shape.shape)
                        {
                            datum.Shape.color = Color.Lerp(read, shape.shape.Color / shape.shape.Pixels, Camera.Unite) * Camera.Border;
                            return;
                        }

                datum.Shape.color = Color.Lerp(read, shape.shape.Color / shape.shape.Pixels, Camera.Unite) * Camera.Inside;
            }
            else
            {
                // Pixel is now invalid in its shape. Remove it.
                shape.shape.Pixels--;
                shape.shape.Color -= shape.add;
                shape = (read, default, default);
            }
        }

        void UpdateRead(ref Datum datum, Color color)
        {
            color = Color.Lerp(color, color.Polarize(), Camera.Polarize.Pre);
            color *= Camera.Multiply.Pre;
            color.r = Mathf.Pow(color.r, Camera.Contrast);
            color.g = Mathf.Pow(color.g, Camera.Contrast);
            color.b = Mathf.Pow(color.b, Camera.Contrast);
            color *= Camera.Multiply.Post;

            var valid = Valid(color, Camera.Threshold);
            datum.Read = (valid ? Color.Lerp(color, color.Polarize(), Camera.Polarize.Post) : default, valid);
        }

        void UpdateBlur(ref Datum datum, TimeSpan delta)
        {
            datum.Last = Color.Lerp(datum.Last, datum.Read.color + datum.Shape.color, Mathf.Clamp01(Camera.Jitter * (float)delta.TotalSeconds));
        }

        static bool Valid(Color color, float threshold) => color.r >= threshold || color.g >= threshold || color.b >= threshold;

        static int Snap(int note, int[] notes)
        {
            var source = note % 12;
            var target = notes.OrderBy(note => Math.Abs(note - source)).FirstOrDefault();
            return note - source + target;
        }
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.C)) _calibrate = !_calibrate;
        if (Input.GetKeyDown(KeyCode.F)) _fps = !_fps;

        if (transform is RectTransform rectangle)
        {
            if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
            {
                if (Input.GetKeyDown(KeyCode.Space)) rectangle.localEulerAngles = rectangle.localEulerAngles.With(x: 0f, y: 0f);
                if (Input.GetKey(KeyCode.LeftArrow)) rectangle.localEulerAngles += new Vector3(0f, -1f, 0f);
                if (Input.GetKey(KeyCode.RightArrow)) rectangle.localEulerAngles += new Vector3(0f, 1f, 0f);
                if (Input.GetKey(KeyCode.DownArrow)) rectangle.localEulerAngles += new Vector3(-1f, 0f, 0f);
                if (Input.GetKey(KeyCode.UpArrow)) rectangle.localEulerAngles += new Vector3(1f, 0f, 0f);
            }
            else if (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt))
            {
                if (Input.GetKeyDown(KeyCode.Space)) rectangle.localEulerAngles = rectangle.localEulerAngles.With(z: 0f);
                if (Input.GetKey(KeyCode.LeftArrow)) rectangle.localEulerAngles += new Vector3(0f, 0f, -1f);
                if (Input.GetKey(KeyCode.RightArrow)) rectangle.localEulerAngles += new Vector3(0f, 0f, 1f);
            }
            else
            {
                var sign = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl) ? -1f : 1f;
                if (Input.GetKeyDown(KeyCode.Space)) { rectangle.offsetMin = default; rectangle.offsetMax = default; }
                if (Input.GetKey(KeyCode.LeftArrow)) rectangle.offsetMin += new Vector2(-1f, 0f) * sign;
                if (Input.GetKey(KeyCode.RightArrow)) rectangle.offsetMax += new Vector2(1f, 0f) * sign;
                if (Input.GetKey(KeyCode.DownArrow)) rectangle.offsetMin += new Vector2(0f, -1f) * sign;
                if (Input.GetKey(KeyCode.UpArrow)) rectangle.offsetMax += new Vector2(0f, 1f) * sign;
            }
        }
    }
}
