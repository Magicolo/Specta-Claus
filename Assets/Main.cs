using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
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
        [Range(0f, 1f)]
        public float Width = 0f;
        [ColorUsage(true, true)]
        public Color Lines = Color.white.With(a: 0.5f);
        public InstrumentSettings[] Instruments;
        public AudioSource Source;
    }

    [Serializable]
    public sealed class CursorSettings
    {
        [ColorUsage(true, true)]
        public Color Color;
        public float Speed = 0.25f;
        [Range(0f, 1f)]
        public float Blend = 0.25f;
    }

    [Serializable]
    public sealed class InstrumentSettings
    {
        public AudioClip[] Clips;
    }

    sealed class Sound
    {
        public AudioClip Clip;
        public float Volume;
        public float Pitch;
        public float Pan;
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

    bool _click;
    readonly Stack<AudioSource> _sources = new();

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
        var camera = (
            texture,
            buffer: new NativeArray<ARGB>(width * height, Allocator.Persistent),
            last: new Color[pixels.Length],
            read: new ARGB[pixels.Length],
            write: new ARGB[pixels.Length]);
        var lines = (tempo: 0, duration: 0, width: 0f, color: default(Color), pixels: new Color[pixels.Length]);
        var fps = (deltas: default(TimeSpan[]), index: 0);
        var particles = new (Vector2 velocity, Color color, TimeSpan delta)[pixels.Length];
        var sounds = (add: new List<Sound>(), play: new List<Sound>());
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
            UpdateInput();
            UpdateFPS(current - time);

            for (int i = 0; i < 10 && current - time >= delta; i++, time += delta)
            {
                var toBeat = (double)Music.Beats / width;
                var toColumn = (double)width / Music.Beats;
                var cursorBeat = time.TotalMinutes * Music.Tempo % Music.Beats;
                var cursorColumn = cursorBeat * toColumn;
                var process = Task.Run(() =>
                {
                    UpdateLines();
                    UpdateParticles(delta);
                    UpdatePixels(cursorColumn, delta, time);
                });

                var request = AsyncGPUReadback.RequestIntoNativeArray(ref camera.buffer, texture);

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
            while (current - time >= delta) time += delta;
        }

        void UpdateInput()
        {
            if (_click.Take())
            {
                var position = Camera.Camera.ScreenToViewportPoint(Input.mousePosition);
                position.x *= width;
                position.y *= height;
                Emit(position, new Color(random.NextFloat(), random.NextFloat(), random.NextFloat(), 1f), 8, 64);
            }
        }

        void UpdateLines()
        {
            if (lines.tempo.Change(Music.Tempo) |
                lines.duration.Change(Music.Beats) |
                lines.width.Change(Music.Width) |
                lines.color.Change(Music.Lines))
            {
                for (int i = 0; i < lines.pixels.Length; i++)
                {
                    var column = i % width;
                    var beat = column * (double)Music.Beats / width;
                    var beatLine = beat - Math.Floor(beat) < Music.Width ? Music.Lines : default;
                    var bar = beat % 4;
                    var barLine = bar - Math.Floor(bar) < Music.Width ? Music.Lines : default;
                    lines.pixels[i] = beatLine + barLine;
                }
            }
        }

        void UpdateParticles(TimeSpan delta)
        {
            var friction = 1f - Mathf.Clamp01(Particle.Friction * (float)delta.TotalSeconds);
            var fade = 1f - Mathf.Clamp01(Particle.Fade * (float)delta.TotalSeconds);
            for (int i = 0; i < particles.Length; i++)
            {
                ref var source = ref particles[i];
                source.velocity *= friction;
                source.color *= fade;

                var position = new Vector2(i % width, i / width) + source.velocity * (float)source.delta.TotalSeconds;
                var index = (int)position.x + (int)position.y * width;
                if (i == index && source.velocity.magnitude > 0.1f) source.delta += delta;
                else source.delta = default;

                if (index >= 0 && index < particles.Length)
                {
                    ref var target = ref particles[index];
                    target.velocity += source.velocity.Take();
                    target.color = Color.Lerp(source.color, target.color, 0.5f);
                }
            }
        }

        void UpdatePixels(double column, TimeSpan delta, TimeSpan time)
        {
            for (int i = 0; i < pixels.Length; i++)
            {
                var x = i % width;
                var emit = x == (int)column;
                var cursor = emit ? Cursor.Color.ShiftHue((float)(time.TotalSeconds % 1.0) * Cursor.Speed) : default;
                var read = Adjust(camera.read[i]);
                var blur = camera.last[i] = Color.Lerp(camera.last[i], read + cursor, Mathf.Clamp01(Camera.Jitter * (float)delta.TotalSeconds));
                var pixel = cursor + lines.pixels[i] + particles[i].color + blur;
                pixels[i] = pixel.Finite();

                if (emit)
                {
                    var ratio = Mathf.Pow(Math.Max(Math.Max(read.r, read.g), read.b), Particle.Power);
                    var radius = Mathf.Lerp(Particle.Radius.x, Particle.Radius.y, ratio);
                    var count = Mathf.RoundToInt(Mathf.Lerp(Particle.Count.x, Particle.Count.y, ratio));
                    var position = new Vector2(x, i / width);
                    Emit(new Vector2(x, i / width), Color.Lerp(read, cursor, Cursor.Blend), radius, count);
                    Sound(position, read);
                }
            }
        }

        void UpdateFPS(TimeSpan delta)
        {
            if (FPS.enabled = Input.GetKey(KeyCode.F))
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

        void Emit(Vector2 position, Color color, float radius, int count)
        {
            var shift = color.ShiftHue(Particle.Shift);
            for (int i = 0; i < count; i++)
            {
                var direction = new Vector2(random.NextFloat(-radius, radius * Particle.Forward), random.NextFloat(-radius, radius));
                var x = (int)(position.x + direction.x);
                var y = (int)(position.y + direction.y);
                var index = x + y * width;
                if (index >= 0 && index < particles.Length)
                {
                    ref var target = ref particles[x + y * width];
                    target.velocity += direction * random.NextFloat(Particle.Speed.x, Particle.Speed.y);
                    target.color = Color.Lerp(target.color, target.color.Polarize(Particle.Polarize) + shift, Particle.Shine);
                }
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

        void Sound(Vector2 position, Color color)
        {
            Color.RGBToHSV(color, out var hue, out var saturation, out var value);
            if (value < Music.Threshold) return;

            var index = (int)(position.x + position.y * width);
            var note = Snap((int)(position.y / height * 80f), _pentatonic);
            if (Music.Instruments.TryAt((int)(hue * Music.Instruments.Length), out var instrument) &&
                instrument.Clips.TryAt(note / 12, out var clip))
                sounds.add.Add(new Sound
                {
                    Clip = clip,
                    Volume = value * value,
                    Pitch = Mathf.Pow(2, note % 12 / 12f),
                    Pan = Mathf.Clamp01(position.x / width) * 2f - 1f,
                });
        }

        Color Adjust(Color color)
        {
            color = Color.Lerp(color, color.Polarize(), Camera.Polarize.Pre);
            color *= Camera.Multiply.Pre;
            color.r = Mathf.Pow(color.r, Camera.Contrast);
            color.g = Mathf.Pow(color.g, Camera.Contrast);
            color.b = Mathf.Pow(color.b, Camera.Contrast);
            color *= Camera.Multiply.Post;
            return color.r < Camera.Threshold && color.g < Camera.Threshold && color.b < Camera.Threshold ?
                default : Color.Lerp(color, color.Polarize(), Camera.Polarize.Post);
        }

        static int Snap(int note, int[] notes)
        {
            var source = note % 12;
            var target = notes.OrderBy(note => Math.Abs(note - source)).FirstOrDefault();
            return note - source + target;
        }
    }

    void Update() => _click |= Input.GetMouseButtonDown(0);
}
