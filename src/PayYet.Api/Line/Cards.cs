using PayYet.Config;

namespace PayYet.Line;

/// <summary>Brand colours shared by every Flex card (same palette as the web app).</summary>
public static class Theme
{
    public const string Orange = "#FF8A00";
    public const string OrangeDeep = "#C25E00";
    public const string Brown = "#3B1F0A";
    public const string Muted = "#7A5A3F";
    public const string Line = "#F1DCC0";
    public const string Cream = "#FFF8EC";
    public const string Green = "#1A9C4A";
    public const string GreenSoft = "#E3F5EA";
    public const string Amber = "#B25A00";
    public const string AmberSoft = "#FFF1D6";
    public const string Red = "#D9480F";
    public const string RedSoft = "#FFE8E0";
    public const string Grey = "#6B7280";
    public const string GreySoft = "#EEF0F2";
}

public enum RowState { Pending, Submitted, Paid, Waived }

/// <summary>Flex card builders. Every card has the PayYet logo header and the same look, so messages feel like one app.</summary>
public static class Cards
{
    public static string LogoUrl(AppSettings s) => $"{s.App.WebBaseUrl.TrimEnd('/')}/img/logo-flex.png";

    public record Row(string Name, RowState State, long RemainingSatang);
    public record ChargeLine(string Title, long TotalSatang);

    // ───────── building blocks ─────────

    private static object Header(string logoUrl, string kicker, string title, string color) => new
    {
        type = "box",
        layout = "horizontal",
        backgroundColor = color,
        paddingAll = "14px",
        spacing = "md",
        alignItems = "center",
        contents = new object[]
        {
            new
            {
                type = "box", layout = "vertical", flex = 0, backgroundColor = "#FFFFFF", cornerRadius = "12px", paddingAll = "3px",
                contents = new object[] { new { type = "image", url = logoUrl, size = "xxs", aspectRatio = "1:1", aspectMode = "fit" } }
            },
            new
            {
                type = "box", layout = "vertical", flex = 1, spacing = "xs",
                contents = new object[]
                {
                    new { type = "text", text = Msg.Trunc(kicker, 40), size = "xs", color = "#FFFFFF", wrap = true },
                    new { type = "text", text = Msg.Trunc(title, 60), size = "md", weight = "bold", color = "#FFFFFF", wrap = true }
                }
            }
        }
    };

    private static object Pill(string text, string fg, string bg) => new
    {
        type = "box",
        layout = "vertical",
        flex = 0,
        backgroundColor = bg,
        cornerRadius = "12px",
        paddingTop = "3px",
        paddingBottom = "3px",
        paddingStart = "9px",
        paddingEnd = "9px",
        contents = new object[] { new { type = "text", text, size = "xs", weight = "bold", color = fg, align = "center" } }
    };

    private static object Bar(int percent)
    {
        var fill = Math.Clamp(percent, 0, 100);
        var inner = new List<object>();
        if (fill > 0)
            inner.Add(new { type = "box", layout = "vertical", width = $"{Math.Max(fill, 3)}%", height = "6px", backgroundColor = Theme.Green, cornerRadius = "3px", contents = new object[] { new { type = "filler" } } });
        else
            inner.Add(new { type = "filler" });
        return new { type = "box", layout = "horizontal", height = "6px", backgroundColor = Theme.Line, cornerRadius = "3px", contents = inner.ToArray() };
    }

    private static object Button(string label, object action, string style, string? color = null) =>
        color == null
            ? new { type = "button", style, height = "md", action }
            : (object)new { type = "button", style, height = "md", color, action };

    // ───────── bill card (group card / private card / reminder) ─────────

    /// <param name="kind">"new", "added", "reminder" or "undone": only changes the header colour and kicker.</param>
    public static object Bill(AppSettings s, string kind, string headline, string creatorName,
        IReadOnlyList<ChargeLine> charges, IReadOnlyList<Row> rows, string payUrl)
    {
        var accent = kind switch { "reminder" => Theme.Red, "undone" => Theme.Grey, _ => Theme.Orange };
        var total = rows.Count;
        var paid = rows.Count(r => r.State is RowState.Paid or RowState.Waived);
        var remaining = rows.Where(r => r.State is not (RowState.Paid or RowState.Waived)).Sum(r => r.RemainingSatang);
        var percent = total == 0 ? 0 : paid * 100 / total;

        var body = new List<object>();
        foreach (var c in charges.Take(8))
            body.Add(new
            {
                type = "box", layout = "horizontal", spacing = "sm",
                contents = new object[]
                {
                    new { type = "text", text = Msg.Trunc(c.Title, 40), size = "sm", color = Theme.Brown, wrap = true, flex = 5 },
                    new { type = "text", text = "฿" + PayYet.Domain.Money.Format(c.TotalSatang), size = "sm", weight = "bold", color = Theme.Brown, align = "end", flex = 3 }
                }
            });
        if (charges.Count > 8)
            body.Add(new { type = "text", text = $"… และอีก {charges.Count - 8} รายการ", size = "xs", color = Theme.Muted });

        body.Add(new { type = "separator", margin = "md", color = Theme.Line });
        body.Add(new
        {
            type = "box", layout = "horizontal", margin = "md",
            contents = new object[]
            {
                new
                {
                    type = "box", layout = "vertical", flex = 1,
                    contents = new object[]
                    {
                        new { type = "text", text = remaining > 0 ? "ยังค้างจ่าย" : "ครบแล้ว 🎉", size = "xs", color = Theme.Muted },
                        new { type = "text", text = "฿" + PayYet.Domain.Money.Format(remaining), size = "xl", weight = "bold", color = remaining > 0 ? accent : Theme.Green }
                    }
                },
                new
                {
                    type = "box", layout = "vertical", flex = 1, alignItems = "flex-end",
                    contents = new object[]
                    {
                        new { type = "text", text = "จ่ายแล้ว", size = "xs", color = Theme.Muted, align = "end" },
                        new { type = "text", text = $"{paid}/{total} คน", size = "md", weight = "bold", color = Theme.Brown, align = "end" }
                    }
                }
            }
        });
        body.Add(new { type = "box", layout = "vertical", margin = "sm", contents = new object[] { Bar(percent) } });

        body.Add(new { type = "separator", margin = "md", color = Theme.Line });
        foreach (var r in rows.Take(30))
        {
            object status = r.State switch
            {
                RowState.Paid => Pill("✅ จ่ายแล้ว", Theme.Green, Theme.GreenSoft),
                RowState.Waived => Pill("ยกหนี้", Theme.Grey, Theme.GreySoft),
                RowState.Submitted => Pill("⏳ รอตรวจ", Theme.Amber, Theme.AmberSoft),
                _ => Pill("฿" + PayYet.Domain.Money.Format(r.RemainingSatang), Theme.Red, Theme.RedSoft)
            };
            body.Add(new
            {
                type = "box", layout = "horizontal", margin = "md", alignItems = "center", spacing = "sm",
                contents = new object[]
                {
                    new { type = "text", text = Msg.Trunc(r.Name, 30), size = "sm", color = Theme.Brown, wrap = true, flex = 1 },
                    status
                }
            });
        }
        if (rows.Count > 30)
            body.Add(new { type = "text", text = $"… และอีก {rows.Count - 30} คน", size = "xs", color = Theme.Muted, margin = "md" });

        var bubble = new
        {
            type = "bubble",
            size = "mega",
            header = new { type = "box", layout = "vertical", paddingAll = "0px", contents = new object[] { Header(LogoUrl(s), $"เก็บโดย {creatorName}", headline, accent) } },
            body = new { type = "box", layout = "vertical", paddingAll = "16px", contents = body.ToArray() },
            footer = new
            {
                type = "box", layout = "vertical", paddingAll = "12px", backgroundColor = Theme.Cream,
                contents = new object[] { Button("จ่ายเงิน", new { type = "uri", label = "จ่ายเงิน", uri = payUrl }, "primary", accent) }
            }
        };
        return Msg.Flex($"{headline} · เก็บโดย {creatorName}", bubble);
    }

    // ───────── "my balance" card (shown in a group: only the asker's own amounts) ─────────

    public record OwedLine(string CollectorName, RowState State, long RemainingSatang);

    public static object MyBalance(AppSettings s, string name, IReadOnlyList<OwedLine> lines, string oweUrl)
    {
        var total = lines.Where(l => l.State != RowState.Submitted).Sum(l => l.RemainingSatang);
        var body = new List<object>();
        if (lines.Count == 0)
        {
            body.Add(new { type = "text", text = "ไม่มียอดค้างในกลุ่มนี้ 🎉", size = "md", weight = "bold", color = Theme.Green, wrap = true });
        }
        else
        {
            body.Add(new { type = "text", text = "ยังค้างจ่ายรวม", size = "xs", color = Theme.Muted });
            body.Add(new { type = "text", text = "฿" + PayYet.Domain.Money.Format(total), size = "xxl", weight = "bold", color = total > 0 ? Theme.Red : Theme.Amber });
            body.Add(new { type = "separator", margin = "md", color = Theme.Line });
            foreach (var l in lines.Take(10))
            {
                object status = l.State == RowState.Submitted
                    ? Pill("⏳ รอตรวจ", Theme.Amber, Theme.AmberSoft)
                    : Pill("฿" + PayYet.Domain.Money.Format(l.RemainingSatang), Theme.Red, Theme.RedSoft);
                body.Add(new
                {
                    type = "box", layout = "horizontal", margin = "md", alignItems = "center", spacing = "sm",
                    contents = new object[]
                    {
                        new { type = "text", text = "บิลของ " + Msg.Trunc(l.CollectorName, 24), size = "sm", color = Theme.Brown, wrap = true, flex = 1 },
                        status
                    }
                });
            }
            if (lines.Count > 10)
                body.Add(new { type = "text", text = $"… และอีก {lines.Count - 10} รายการ", size = "xs", color = Theme.Muted, margin = "md" });
        }

        var bubble = new
        {
            type = "bubble",
            size = "kilo",
            header = new { type = "box", layout = "vertical", paddingAll = "0px", contents = new object[] { Header(LogoUrl(s), "ยอดค้างของ", name, lines.Count == 0 ? Theme.Green : Theme.Orange) } },
            body = new { type = "box", layout = "vertical", paddingAll = "16px", contents = body.ToArray() },
            footer = lines.Count == 0 ? null : (object)new
            {
                type = "box", layout = "vertical", paddingAll = "12px", backgroundColor = Theme.Cream,
                contents = new object[] { Button("ดูและจ่ายเงิน", new { type = "uri", label = "ดูและจ่ายเงิน", uri = oweUrl }, "primary", Theme.Orange) }
            }
        };
        return Msg.Flex($"ยอดค้างของ {name}", bubble);
    }

    // ───────── confirm card (before a bill is created) ─────────

    public record ConfirmInfo(string Title, IReadOnlyList<string> Calc, IReadOnlyList<string> Info, IReadOnlyList<string> Warnings);

    public static object Confirm(AppSettings s, ConfirmInfo c)
    {
        var body = new List<object>
        {
            new { type = "text", text = Msg.Trunc(c.Title, 60), size = "lg", weight = "bold", color = Theme.Brown, wrap = true }
        };
        foreach (var line in c.Calc)
            body.Add(new { type = "text", text = line, size = "md", weight = "bold", color = Theme.Orange, wrap = true, margin = "md" });
        foreach (var line in c.Info)
            body.Add(new { type = "text", text = line, size = "sm", color = Theme.Muted, wrap = true, margin = "sm" });
        foreach (var line in c.Warnings)
            body.Add(new
            {
                type = "box", layout = "vertical", margin = "md", backgroundColor = Theme.AmberSoft, cornerRadius = "8px", paddingAll = "8px",
                contents = new object[] { new { type = "text", text = line, size = "xs", color = Theme.Amber, wrap = true } }
            });

        var buttons = new List<object>
        {
            Button("ยืนยัน", new { type = "postback", label = "ยืนยัน", data = "act=confirm" }, "primary", Theme.Orange)
        };
        buttons.Add(Button("ยกเลิก", new { type = "postback", label = "ยกเลิก", data = "act=cancel" }, "link", Theme.Grey));

        var bubble = new
        {
            type = "bubble",
            size = "mega",
            header = new { type = "box", layout = "vertical", paddingAll = "0px", contents = new object[] { Header(LogoUrl(s), "PayYet", "ยืนยันการเก็บเงิน", Theme.Orange) } },
            body = new { type = "box", layout = "vertical", paddingAll = "16px", contents = body.ToArray() },
            footer = new { type = "box", layout = "vertical", paddingAll = "12px", spacing = "sm", backgroundColor = Theme.Cream, contents = buttons.ToArray() }
        };
        return Msg.Flex("ยืนยันการเก็บเงิน: " + c.Title, bubble);
    }

    // ───────── slip waiting for the collector ─────────

    public static object SlipReview(AppSettings s, string debtorName, long amountSatang, string billTitle, string imageUrl, long paymentId)
    {
        var bubble = new
        {
            type = "bubble",
            size = "mega",
            header = new { type = "box", layout = "vertical", paddingAll = "0px", contents = new object[] { Header(LogoUrl(s), "มีสลิปรอตรวจ", debtorName, Theme.Orange) } },
            hero = new
            {
                type = "image", url = imageUrl, size = "full", aspectRatio = "3:4", aspectMode = "fit", backgroundColor = Theme.GreySoft,
                action = new { type = "uri", label = "ดูสลิป", uri = imageUrl }
            },
            body = new
            {
                type = "box", layout = "vertical", paddingAll = "16px", spacing = "xs",
                contents = new object[]
                {
                    new { type = "text", text = "฿" + PayYet.Domain.Money.Format(amountSatang), size = "xxl", weight = "bold", color = Theme.Orange },
                    new { type = "text", text = Msg.Trunc(billTitle, 60), size = "sm", color = Theme.Muted, wrap = true },
                    new { type = "text", text = "ตรวจยอดในสลิปให้ตรงก่อนกดยืนยัน", size = "xs", color = Theme.Muted, wrap = true, margin = "md" }
                }
            },
            footer = new
            {
                type = "box", layout = "vertical", paddingAll = "12px", spacing = "sm", backgroundColor = Theme.Cream,
                contents = new object[]
                {
                    Button("✅ ยืนยันได้รับเงินแล้ว", new { type = "postback", label = "ยืนยัน", data = $"act=approve&p={paymentId}" }, "primary", Theme.Green),
                    Button("ปฏิเสธ", new { type = "postback", label = "ปฏิเสธ", data = $"act=reject&p={paymentId}" }, "secondary")
                }
            }
        };
        return Msg.Flex($"สลิปรอตรวจ: {debtorName} ฿{PayYet.Domain.Money.Format(amountSatang)}", bubble);
    }

    // ───────── small notices ─────────

    /// <summary>"Bill created" notice with optional undo buttons (postbacks).</summary>
    public static object Created(AppSettings s, string title, string detail, IEnumerable<(string Label, string Data)> undo)
    {
        var buttons = undo.Take(4).Select(u => Button(Msg.Trunc(u.Label, 20), new { type = "postback", label = Msg.Trunc(u.Label, 20), data = u.Data }, "link", Theme.Grey)).ToArray();
        var bubble = new
        {
            type = "bubble",
            size = "kilo",
            header = new { type = "box", layout = "vertical", paddingAll = "0px", contents = new object[] { Header(LogoUrl(s), "PayYet", title, Theme.Green) } },
            body = new
            {
                type = "box", layout = "vertical", paddingAll = "16px", spacing = "sm",
                contents = new object[]
                {
                    new { type = "text", text = Msg.Trunc(detail, 300), size = "sm", color = Theme.Brown, wrap = true },
                    new { type = "text", text = "ยกเลิกได้ภายใน 10 นาที (ถ้ายังไม่มีใครจ่าย)", size = "xs", color = Theme.Muted, wrap = true, margin = "md" }
                }
            },
            footer = buttons.Length == 0 ? null : new { type = "box", layout = "vertical", paddingAll = "8px", backgroundColor = Theme.Cream, contents = buttons }
        };
        return Msg.Flex(title, bubble);
    }

    /// <summary>The button that opens the people-picker page.</summary>
    public static object PickPrompt(AppSettings s, string title, string what, string pickUrl, string? forName = null, bool allowCancel = true)
    {
        var bubble = new
        {
            type = "bubble",
            size = "kilo",
            header = new { type = "box", layout = "vertical", paddingAll = "0px", contents = new object[] { Header(LogoUrl(s), forName == null ? "PayYet" : $"คำสั่งของ {forName}", "เลือกคนที่จะเรียกเก็บ", Theme.Orange) } },
            body = new
            {
                type = "box", layout = "vertical", paddingAll = "16px", spacing = "sm",
                contents = new object[]
                {
                    new { type = "text", text = Msg.Trunc(title, 60), size = "md", weight = "bold", color = Theme.Brown, wrap = true },
                    new { type = "text", text = what, size = "lg", weight = "bold", color = Theme.Orange, wrap = true }
                }
            },
            footer = new
            {
                type = "box", layout = "vertical", paddingAll = "12px", spacing = "sm", backgroundColor = Theme.Cream,
                contents = allowCancel
                    ? new object[]
                    {
                        Button("เลือกคน", new { type = "uri", label = "เลือกคน", uri = pickUrl }, "primary", Theme.Orange),
                        Button("ยกเลิก", new { type = "postback", label = "ยกเลิก", data = "act=cancel" }, "link", Theme.Grey)
                    }
                    : new object[] { Button("เลือกคน", new { type = "uri", label = "เลือกคน", uri = pickUrl }, "primary", Theme.Orange) }
            }
        };
        return Msg.Flex("เลือกคนที่จะเรียกเก็บ: " + title, bubble);
    }

    /// <summary>Entry point to the "manage bills" page (record cash, waive, cancel).</summary>
    public static object ManagePrompt(AppSettings s, int openBills, long unpaidSatang, string manageUrl)
    {
        var bubble = new
        {
            type = "bubble",
            size = "kilo",
            header = new { type = "box", layout = "vertical", paddingAll = "0px", contents = new object[] { Header(LogoUrl(s), "PayYet", "จัดการบิลของฉัน", Theme.Orange) } },
            body = new
            {
                type = "box", layout = "vertical", paddingAll = "16px", spacing = "sm",
                contents = new object[]
                {
                    new { type = "text", text = $"บิลที่ยังเปิดอยู่ {openBills} บิล", size = "md", weight = "bold", color = Theme.Brown },
                    new { type = "text", text = "ยังค้างจ่ายรวม", size = "xs", color = Theme.Muted, margin = "md" },
                    new { type = "text", text = "฿" + PayYet.Domain.Money.Format(unpaidSatang), size = "xxl", weight = "bold", color = unpaidSatang > 0 ? Theme.Orange : Theme.Green },
                    new { type = "text", text = "ดูว่าใครจ่ายแล้ว บันทึกเงินสด ยกหนี้ หรือยกเลิกรายการ", size = "xs", color = Theme.Muted, wrap = true, margin = "md" }
                }
            },
            footer = new
            {
                type = "box", layout = "vertical", paddingAll = "12px", backgroundColor = Theme.Cream,
                contents = new object[] { Button("เปิดหน้าจัดการ", new { type = "uri", label = "เปิดหน้าจัดการ", uri = manageUrl }, "primary", Theme.Orange) }
            }
        };
        return Msg.Flex("จัดการบิลของฉัน", bubble);
    }
}
