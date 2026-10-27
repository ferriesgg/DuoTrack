using System.Collections.ObjectModel;

namespace DuoTrack.Client;

internal static class DeviceListUpdater
{
    public static bool Synchronize(ObservableCollection<DeviceInfo> current, IReadOnlyList<DeviceInfo> next)
    {
        if (current.SequenceEqual(next)) return false;
        for (var i = 0; i < next.Count; i++)
        {
            var index = -1;
            for (var j = i; j < current.Count; j++)
                if (current[j].Id == next[i].Id) { index = j; break; }
            if (index < 0) current.Insert(i, next[i]);
            else
            {
                if (index != i) current.Move(index, i);
                if (current[i] != next[i]) current[i] = next[i];
            }
        }
        while (current.Count > next.Count) current.RemoveAt(current.Count - 1);
        return true;
    }
}
