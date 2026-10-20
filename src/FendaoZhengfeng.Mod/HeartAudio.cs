using Godot;
using MegaCrit.Sts2.Core.Saves;

namespace FendaoZhengfeng.Mod;

internal static class HeartAudio
{
    private static readonly Dictionary<int, AudioStreamWav> sounds = [];
    internal static void Play(Node parent, int kind)
    {
        if (!parent.IsInsideTree()) return;
        var settings = SaveManager.Instance.SettingsSave;
        float volume = Math.Clamp(settings.VolumeMaster * settings.VolumeSfx, 0, 1);
        if (volume <= 0) return;
        if (!sounds.TryGetValue(kind, out var stream))
        {
            const int rate = 22050;
            double duration = kind == 2 ? 2.4 : kind == 1 ? 0.7 : 0.32;
            var samples = new byte[(int)(rate * duration) * 2];
            for (int i = 0; i < samples.Length / 2; i++)
            {
                double t = (double)i / rate;
                double envelope = Math.Min(1, t * 90) * Math.Exp(-t * (kind == 2 ? 2.3 : 11));
                double frequency = kind == 2 ? 34 : kind == 1 ? 52 : 62;
                double signal = Math.Sin(Math.Tau * (frequency * t - 12 * t * t)) + 0.22 * Math.Sin(Math.Tau * 118 * t);
                if (kind == 2) signal += Math.Sin(17983 * t) * Math.Sin(24557 * t) * 0.35;
                short sample = (short)Math.Clamp(signal * envelope * 14000, short.MinValue, short.MaxValue);
                samples[i * 2] = (byte)(sample & 255); samples[i * 2 + 1] = (byte)((sample >> 8) & 255);
            }
            sounds[kind] = stream = new AudioStreamWav { Data = samples, MixRate = rate, Format = AudioStreamWav.FormatEnum.Format16Bits, Stereo = false };
        }
        var player = new AudioStreamPlayer { Stream = stream, VolumeDb = Mathf.LinearToDb(volume) - 13 };
        parent.AddChild(player); player.Finished += player.QueueFree; player.Play();
    }
}
