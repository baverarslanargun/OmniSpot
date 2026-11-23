using System.ComponentModel;
using SmartFileLauncher.Core.Application.Settings;

namespace SmartFileLauncher.UI.Views;

public sealed class IndexedLocationRow : INotifyPropertyChanged
{
    private IndexVolumeMethod _method;

    internal IndexedLocationRow(string path, string volume, IndexVolumeMethod method, bool mftAvailable)
    {
        Path = path;
        Volume = volume;
        MftAvailable = mftAvailable;
        _method = Coerce(method);
    }

    public string Path { get; }
    public string Volume { get; }
    public bool MftAvailable { get; }

    public IndexVolumeMethod Method
    {
        get => _method;
        set
        {
            var method = Coerce(value);
            if (method == _method) return;
            _method = method;
            PropertyChanged?.Invoke(this, new(nameof(Method)));
            PropertyChanged?.Invoke(this, new(nameof(MethodIndex)));
            PropertyChanged?.Invoke(this, new(nameof(MethodHint)));
        }
    }

    public int MethodIndex
    {
        get => (int)_method;
        set
        {
            if (value >= 0) Method = (IndexVolumeMethod)value;
        }
    }

    public string MethodHint => _method switch
    {
        IndexVolumeMethod.Mft => "MFT: diski OmniSpot hizmetiyle okur. NTFS ve açık Windows değişiklik günlüğü ister.",
        IndexVolumeMethod.Folders => "Klasör taraması: klasörleri gezer, değişiklikleri Windows klasör izleyicisiyle izler.",
        _ => MftAvailable
            ? "Otomatik: önce MFT denenir; Windows değişiklik günlüğü kapalıysa klasör taraması kullanılır."
            : "Otomatik: bu konum NTFS değil, klasör taraması kullanılır."
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private IndexVolumeMethod Coerce(IndexVolumeMethod method) =>
        !Enum.IsDefined(method) ? IndexVolumeMethod.Automatic
        : method == IndexVolumeMethod.Mft && !MftAvailable ? IndexVolumeMethod.Automatic
        : method;
}
