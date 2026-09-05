using NAudio.Wave;
using System.Collections.Generic;

namespace VoiceGuard;

public static class AudioDeviceEnumerator
{
    public static List<AudioDeviceInfo> GetInputs()
    {
        var list = new List<AudioDeviceInfo>();
        for (int i = 0; i < WaveIn.DeviceCount; i++)
        {
            var c = WaveIn.GetCapabilities(i);
            list.Add(new AudioDeviceInfo(i, c.ProductName));
        }
        return list;
    }

    public static List<AudioDeviceInfo> GetOutputs()
    {
        var list = new List<AudioDeviceInfo>();
        for (int i = 0; i < WaveOut.DeviceCount; i++)
        {
            var c = WaveOut.GetCapabilities(i);
            list.Add(new AudioDeviceInfo(i, c.ProductName));
        }
        return list;
    }
}
