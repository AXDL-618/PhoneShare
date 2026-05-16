using System.IO;
namespace PhoneShareReceiver;

public sealed class ReceiverSettings
{
    public string DeviceId { get; set; } = Guid.NewGuid().ToString("N");
    public string DeviceName { get; set; } = Environment.MachineName;
    public string Token { get; set; } = SecurityUtil.CreateToken();
    public string SaveFolder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Downloads",
        "PhoneShare"
    );
    public int Port { get; set; } = 53318;
    public long MaxFileSizeMb { get; set; } = 4096;
    public bool AutoStart { get; set; } = false;

    public List<PairedPhoneDevice> PairedPhones { get; set; } = new();
}

public sealed class PairedPhoneDevice
{
    public string DeviceId { get; set; } = Guid.NewGuid().ToString("N");

    // 手机端上报的原始设备名，例如 PLA-AL10。
    public string PhoneName { get; set; } = "Android 手机";

    // 电脑端自定义显示名，例如“我的华为手机”“备用机”。只影响电脑端显示，不影响手机端绑定。
    public string CustomName { get; set; } = "";

    public string Manufacturer { get; set; } = "";
    public string AndroidVersion { get; set; } = "";
    public DateTimeOffset FirstPairedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset LastPairedAt { get; set; } = DateTimeOffset.Now;

    public string OriginalName => string.IsNullOrWhiteSpace(PhoneName) ? "Android 手机" : PhoneName.Trim();

    public string DisplayName => string.IsNullOrWhiteSpace(CustomName) ? OriginalName : CustomName.Trim();
}
