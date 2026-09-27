using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace AirTools.Agent
{
    /// WAV in and out for voice: microphone samples → a 16-bit PCM WAV for POST /voice/command; the spoken reply
    /// (audio/wav from Groq Orpheus, or audio/mpeg from the Edge TTS fallback) → an AudioClip.
    public static class WavUtil
    {
        public static byte[] Encode(float[] samples, int channels, int sampleRate) =>
            WavPcm.Encode(samples, 0, samples?.Length ?? 0, channels, sampleRate);

        /// Parses a RIFF/WAVE file (PCM 8/16/24/32-bit or IEEE float 32) into an AudioClip. Null if it isn't one.
        public static AudioClip FromWav(byte[] bytes, string name = "wav")
        {
            var data = WavPcm.Decode(bytes, out int channels, out int rate);
            if (data == null) return null;
            int frames = data.Length / channels;
            var clip = AudioClip.Create(name, frames, channels, rate, false);
            clip.SetData(data, 0);
            return clip;
        }

        /// Decode by mime: audio/wav in memory; audio/mpeg (and anything else) through a temp file and Unity's decoder.
        public static IEnumerator Decode(byte[] bytes, string mime, Action<AudioClip> done)
        {
            var m = (mime ?? "").ToLowerInvariant();
            if (m.Contains("wav") || (bytes.Length > 4 && bytes[0] == 'R' && bytes[1] == 'I'))
            {
                done(FromWav(bytes, "reply"));
                yield break;
            }
            var type = m.Contains("mpeg") || m.Contains("mp3") ? AudioType.MPEG : m.Contains("ogg") ? AudioType.OGGVORBIS : AudioType.UNKNOWN;
            string path = Path.Combine(Application.temporaryCachePath, $"reply-{Guid.NewGuid():N}{(type == AudioType.MPEG ? ".mp3" : ".audio")}");
            File.WriteAllBytes(path, bytes);
            using (var req = UnityWebRequestMultimedia.GetAudioClip("file://" + path, type))
            {
                // fix-ux: a local file, but still on our clock (10 s) so a stuck decode can't hold the reply forever.
                yield return AirTools.Core.HttpDeadline.Send(req, idleSeconds: 0f, label: "decode spoken reply", totalSeconds: 10f);
                done(AirTools.Core.HttpDeadline.Ok(req) ? DownloadHandlerAudioClip.GetContent(req) : null);
            }
            try { File.Delete(path); } catch (Exception) { }
        }
    }
}
