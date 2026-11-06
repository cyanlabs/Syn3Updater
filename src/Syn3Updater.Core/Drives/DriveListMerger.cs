using System.Collections.ObjectModel;

namespace Cyanlabs.Syn3Updater.Core.Drives;

/// <summary>What <see cref="DriveListMerger.Merge"/> changed.</summary>
public sealed class DriveMergeResult
{
    public List<DriveInfoModel> Added { get; } = new();
    public List<DriveInfoModel> Removed { get; } = new();
    /// <summary>Drives whose details changed (free space, mount path, label): old object to the new one that took its place.</summary>
    public Dictionary<DriveInfoModel, DriveInfoModel> Replaced { get; } = new();
    public bool Changed => Added.Count + Removed.Count + Replaced.Count > 0;

    /// <summary>Where a drive that was in the list before ended up: the same object, its replacement, or null when it was removed.</summary>
    public DriveInfoModel? Current(DriveInfoModel before) =>
        Removed.Contains(before) ? null : Replaced.TryGetValue(before, out var now) ? now : before;
}

/// <summary>
///     Brings a dropdown's drive list up to date <em>in place</em>: drives that are still there are left alone, so an open dropdown does not
///     flicker or lose its selection (clearing and refilling the list does). The folder and "Select A Directory..." entries are never touched.
/// </summary>
public static class DriveListMerger
{
    public static string Signature(DriveInfoModel d) =>
        string.Join("|", d.DeviceId, d.MountPath, d.Name, d.Model, d.VolumeName, d.SizeBytes, d.FreeBytes, d.FileSystem, d.PartitionType, d.Encrypted);

    public static DriveMergeResult Merge(ObservableCollection<DriveInfoModel> list, IReadOnlyList<DriveInfoModel> found)
    {
        var result = new DriveMergeResult();
        static bool IsDrive(DriveInfoModel d) => !d.IsFolder && !d.IsChooser;

        var wanted = new List<DriveInfoModel>();
        var seen = new HashSet<string>();
        foreach (var d in found) if (seen.Add(d.DeviceId)) wanted.Add(d);       // one entry per device

        // 1. drop drives that are gone
        for (var i = list.Count - 1; i >= 0; i--)
            if (IsDrive(list[i]) && !seen.Contains(list[i].DeviceId)) { result.Removed.Add(list[i]); list.RemoveAt(i); }

        // 2. walk the wanted drives in order; drives always occupy the leading positions, the pinned entries follow
        for (var i = 0; i < wanted.Count; i++)
        {
            var w = wanted[i];
            if (i < list.Count && IsDrive(list[i]) && list[i].DeviceId == w.DeviceId) { Refresh(i, w); continue; }

            var at = -1;
            for (var j = i + 1; j < list.Count; j++) if (IsDrive(list[j]) && list[j].DeviceId == w.DeviceId) { at = j; break; }
            if (at >= 0) { list.Move(at, i); Refresh(i, w); }
            else { list.Insert(i, w); result.Added.Add(w); }
        }
        return result;

        void Refresh(int index, DriveInfoModel latest)
        {
            if (Signature(list[index]) == Signature(latest)) return;                // unchanged: keep the very same object
            result.Replaced[list[index]] = latest; list[index] = latest;
        }
    }
}
