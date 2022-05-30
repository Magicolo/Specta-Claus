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
        public float Threshold = 0.1f;
        public float Duration = 0.1f;
        public float Fade = 0.1f;
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
        Blur = 4
    }

    static readonly int[] _pentatonic = { 0, 3, 5, 7, 10 };

    [Range(0f, 1f)]
    public float Delta = 0.01f;
    public Vector2 Fade = new(0.1f, 10f);
    public float Explode = 1f;
    public ComputeShader Shader;
    public Material Material;
    public CameraSettings Camera = new();
    public MusicSettings Music = new();
    public CursorSettings Cursor = new();
    public TMP_Text Text;

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
        var deltas = new Queue<float>();
        var clips = Music.Clips
            .GroupBy(clip => clip.name.Split('_')[0]).Select(group => group.ToArray())
            .ToArray();
        var sources = new Stack<AudioSource>();
        var routines = new List<(float key, IEnumerator routine)>();
        var size = new Vector2Int(device.width, device.height);
        var buffer = new NativeArray<Color>(size.x * size.y, Allocator.Persistent);
        var camera = (input: device, output: Texture(size));
        var color = (input: Texture(size), output: Texture(size));
        var velocity = (input: Texture(size), output: Texture(size));
        var blur = (input: Texture(size), output: Texture(size));
        var output = Texture(size);
        var cursorColor = Cursor.Color;

        // Wait until fps has stabilized.
        for (int i = 0; i < 10; i++) yield return null;

        var time = Time.time;
        var request = AsyncGPUReadback.RequestIntoNativeArray(ref buffer, blur.input);
        while (true)
        {
            var buttons = (
                Input.GetKey(KeyCode.Alpha1) || Input.GetKey(KeyCode.Keypad1),
                Input.GetKeyDown(KeyCode.Alpha2) || Input.GetKeyDown(KeyCode.Keypad2),
                Input.GetKey(KeyCode.Alpha3) || Input.GetKey(KeyCode.Keypad3),
                Input.GetKey(KeyCode.Alpha4) || Input.GetKey(KeyCode.Keypad4));
            var delta = Time.time - time;
            while (deltas.Count >= 100) deltas.Dequeue();
            while (deltas.Count < 100) deltas.Enqueue(1f / delta);
            if (Input.GetKeyDown(KeyCode.Tab)) mode = (Modes)((int)(mode + 1) % 5);
            Text.text = mode > 0 || Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift) ?
$@"FPS: {deltas.Average():0.00}
Mode: {mode}
Resolution: {size.x} x {size.y}" : "";

            var toColumn = (double)size.x / Music.Beats;
            var cursorBeat = time / 60f * Music.Tempo % Music.Beats;
            var cursorColumn = (int)(cursorBeat * toColumn);

            Shader.SetInt("Width", size.x);
            Shader.SetInt("Height", size.y);
            Shader.SetTexture(0, "Output", output);
            Shader.SetTexture(0, "CameraInput", camera.input);
            Shader.SetTexture(0, "CameraOutput", camera.output);
            Shader.SetInt("CursorColumn", cursorColumn);
            Shader.SetVector("CursorColor", cursorColor);
            Shader.SetFloat("Time", time);
            Shader.SetFloat("Delta", Delta);
            Shader.SetFloat("Fade", buttons.Item1 ? Fade.y : Fade.x);
            Shader.SetFloat("Explode", buttons.Item2 ? Explode : 0f);

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
                Shader.Dispatch(0, size.x / 8, size.y / 4, 1);
                (velocity.input, velocity.output) = (velocity.output, velocity.input);
                (color.input, color.output) = (color.output, color.input);
                (blur.input, blur.output) = (blur.output, blur.input);
            }
            while (time < Time.time) { time += Delta; Debug.Log($"Skipped a frame."); }

            Material.mainTexture = Input.GetKey(KeyCode.Space) ? output : mode switch
            {
                Modes.None => output,
                Modes.Color => color.output,
                Modes.Velocity => velocity.output,
                Modes.Camera => device,
                Modes.Blur => blur.output,
                _ => default,
            };

            if (request.done)
            {
                var cursorSum = EmitSounds();
                request = AsyncGPUReadback.RequestIntoNativeArray(ref buffer, blur.input);
                PlaySounds();
                Color.RGBToHSV(cursorSum, out var hue, out var saturation, out _);
                cursorColor = cursorColor.MapHSV(color => (
                    Mathf.Lerp(color.h, hue, (float)delta * Cursor.Adapt),
                    Mathf.Lerp(color.s, saturation, (float)delta * Cursor.Adapt),
                    color.v)).Finite().Clamp(0f, 5f);
            }
            yield return null;

            Color EmitSounds()
            {
                var sum = Cursor.Color;
                var pan = Mathf.Clamp01((float)cursorColumn / size.x) * 2f - 1f;
                for (int y = 0; y < size.y; y++)
                {
                    var color = buffer[cursorColumn + y * size.x];
                    sum += color;

                    Color.RGBToHSV(color, out var hue, out var saturation, out var value);
                    if (value < Music.Threshold) continue;

                    var note = Snap((int)((float)y / size.y * 80f), _pentatonic);
                    if (clips.TryAt((int)(hue * clips.Length), out var instrument) && instrument.TryAt(note / 12, out var clip))
                        routines.Add((-value, PlaySound(clip, value * value, Mathf.Pow(2, note % 12 / 12f), pan)));
                }
                return sum;
            }

            void PlaySounds()
            {
                routines.Sort((left, right) => right.key.CompareTo(left.key));
                for (int i = 0; i < Music.Voices && i < routines.Count; i++)
                {
                    var index = (int)(Mathf.Pow(Random.value, 2) * routines.Count);
                    for (int j = index; j < routines.Count; j++)
                    {
                        if (routines[j].routine is IEnumerator routine)
                        {
                            StartCoroutine(routine);
                            routines[j] = default;
                            break;
                        }
                    }
                }
                routines.Clear();
            }

            IEnumerator PlaySound(AudioClip clip, float volume, float pitch, float pan)
            {
                var source = sources.TryPop(out var value) && value ? value : Instantiate(Music.Source);
                source.name = clip.name;
                source.clip = clip;
                source.volume = volume;
                source.pitch = pitch;
                source.panStereo = pan;
                source.Play();

                for (var counter = 0f; counter < Music.Duration && source.isPlaying; counter += Time.deltaTime)
                    yield return null;

                for (var counter = 0f; counter < Music.Fade && source.isPlaying; counter += Time.deltaTime)
                {
                    source.volume = volume * (1f - Mathf.Clamp01(counter / Music.Fade));
                    yield return null;
                }

                source.Stop();
                sources.Push(source);
            }

            static int Snap(int note, int[] notes)
            {
                var source = note % 12;
                var target = notes.OrderBy(note => Math.Abs(note - source)).FirstOrDefault();
                return note - source + target;
            }
        }
    }
}
