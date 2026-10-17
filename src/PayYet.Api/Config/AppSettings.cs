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
    public string WebBaseUrl { get; set; } = "https://payyet.drivetodev.online";   // static web app (landing + LIFF page); also the only CORS origin
    public string LineOaUrl { get; set; } = "";           // https://line.me/R/ti/p/@xxxx - shows the "add friend" button on the landing page
    public string PathBase { get; set; } = "";             // "" on a dedicated (sub)domain; "/lineapi" only when hosted in a sub-folder
}

/// <summary>Optional bank-side slip check. Leave Provider empty to keep checking by hand only.</summary>
public class SlipVerifySection
{
    public string Provider { get; set; } = "";   // "slipok" or empty
    public string BranchId { get; set; } = "";
    public string ApiKey { get; set; } = "";
}

public class JobsSection
{
    public string Key { get; set; } = "";
    public int ReminderHour { get; set; } = 10;        // Asia/Bangkok
}
