namespace PayYet.Line;

/// <summary>Builders for LINE message objects (plain anonymous objects, serialized by LineClient).</summary>
public static class Msg
{
    public static object Text(string text) => new { type = "text", text = Trunc(text, 4900) };

    public static object TextWithPostbackChoices(string text, IEnumerable<(string Label, string Data)> choices) => new
    {
        type = "text",
        text = Trunc(text, 4900),
        quickReply = new
        {
            items = choices.Take(13).Select(c => new
            {
                type = "action",
                action = new { type = "postback", label = Trunc(c.Label, 20), data = c.Data }
            }).ToArray()
        }
    };

    /// <summary>Message with up to 4 postback buttons (template "buttons"; text ≤ 160 chars).</summary>
    public static object Buttons(string altText, string text, IEnumerable<(string Label, string Data)> buttons) => new
    {
        type = "template",
        altText = Trunc(altText, 400),
        template = new
        {
            type = "buttons",
            text = Trunc(text, 160),
            actions = buttons.Take(4).Select(b => (object)new { type = "postback", label = Trunc(b.Label, 20), data = b.Data }).ToArray()
        }
    };

    public static object PostbackAction(string label, string data) => new { type = "postback", label = Trunc(label, 20), data };

    public static object UriAction(string label, string uri) => new { type = "uri", label = Trunc(label, 20), uri };

    /// <summary>Buttons message mixing action types (e.g. a link button and a postback button). Text ≤ 160 chars.</summary>
    public static object ButtonsMixed(string altText, string text, IEnumerable<object> actions) => new
    {
        type = "template",
        altText = Trunc(altText, 400),
        template = new { type = "buttons", text = Trunc(text, 160), actions = actions.Take(4).ToArray() }
    };

    public static object Confirm(string altText, string text, (string Label, string Data) yes, (string Label, string Data) no) => new
    {
        type = "template",
        altText = Trunc(altText, 400),
        template = new
        {
            type = "confirm",
            text = Trunc(text, 240),
            actions = new object[]
            {
                new { type = "postback", label = Trunc(yes.Label, 20), data = yes.Data },
                new { type = "postback", label = Trunc(no.Label, 20), data = no.Data }
            }
        }
    };

    public static object Image(string url) => new { type = "image", originalContentUrl = url, previewImageUrl = url };

    /// <summary>Text message that tags (@mentions) the given LINE users, sent right after a card posted in a group. Null when nobody to tag.</summary>
    public static object? Mention(string prefix, IEnumerable<string> lineUserIds)
    {
        var ids = lineUserIds.Where(i => !string.IsNullOrEmpty(i)).Distinct().Take(100).ToList();
        if (ids.Count == 0) return null;
        var text = new System.Text.StringBuilder(Trunc(prefix, 200));
        var substitution = new Dictionary<string, object>();
        for (var i = 0; i < ids.Count; i++)
        {
            text.Append(" {u").Append(i).Append('}');
            substitution["u" + i] = new { type = "mention", mentionee = new { type = "user", userId = ids[i] } };
        }
        return new { type = "textV2", text = text.ToString(), substitution };
    }

    public static object Flex(string altText, object bubble) => new { type = "flex", altText = Trunc(altText, 400), contents = bubble };

    public static string Trunc(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
