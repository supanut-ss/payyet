namespace PayYet.Config;

/// <summary>Bound from the root of appsettings / environment variables.</summary>
public class AppSettings
{
    public LineSection Line { get; set; } = new();
    public LiffSection Liff { get; set; } = new();
    public AppSection App { get; set; } = new();
    public JobsSection Jobs { get; set; } = new();
    public SlipVerifySection SlipVerify { get; set; } = new();
}

public class LineSection
{
    public string ChannelSecret { get; set; } = "";
    public string ChannelAccessToken { get; set; } = "";
    public string LoginChannelId { get; set; } = "";   // LINE Login channel that owns the LIFF app
    public bool CanListGroupMembers { get; set; }      // true only for verified / premium OAs
}

public class LiffSection
{
    public string Id { get; set; } = "";
}

public class AppSection
{
    public string PublicBaseUrl { get; set; } = "https://api.drivetodev.online";
    public string WebBaseUrl { get; set; } = "https://drivetodev.online/payyet";   // static web app (landing + LIFF page); also the only CORS origin
    public string LineOaUrl { get; set; } = "";           // https://line.me/R/ti/p/@xxxx - shows the "add friend" button on the landing page
    public string PathBase { get; set; } = "";             // "" on a dedicated (sub)domain; "/lineapi" only when hosted in a sub-folder
}

/// <summary>Optional bank-side slip check. Leave Provider empty to keep checking by hand only.</summary>
public class SlipVerifySection
{
    public string Provider { get; set; } = "";   // "slipok" or empty
    public string BranchId { get; set; } = "";
    public string ApiKey { get; set; } = "";

    // Offline check of the QR printed on the slip (no provider needed): CRC, bank code, age, and no re-use of the same transfer.
    public bool QrCheck { get; set; } = true;
    public bool RequireQr { get; set; }       // true = a slip without a readable QR is refused (strict); false = collector checks it by hand
    public int MaxAgeDays { get; set; } = 7;  // slips older than this are refused when their date can be read

    // Amount printed on the slip (read with OCR) vs the amount the payer declared: "warn" (default) sends the slip to the collector with a warning, "reject" refuses a mismatch at once, "off" skips it.
    public string AmountCheck { get; set; } = "warn";
}

public class JobsSection
{
    public string Key { get; set; } = "";
    public int ReminderHour { get; set; } = 10;        // Asia/Bangkok
}
