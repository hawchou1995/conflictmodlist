namespace TeamModVersions;

// One native TMP mesh per slot. Presentation only: never infer quality or replace missing measurements.
internal static class FieldTerminalTheme
{
    internal const int Base=0x212522, Accent=0xF5DE36, Ink=0xFFFFFF, Quiet=0xE1E5E1;
    internal const byte OutlineGray=12;
    internal const float Opacity=0f, OutlineWidth=.20f, Type=25, Pad=3, RuleHeight=.025f, TabWidth=.23f;
    internal static string Format(string source,int seat,bool bot)
    {
        if(string.IsNullOrEmpty(source)) return "";
        var rows=source.Replace('<','＜').Split('\n'); // Keep source text literal when enabling our own markup.
        string Paint(int rgb,string s)=>$"<color=#{rgb:X6}>{s}</color>";
        rows[0]=Paint(bot?Quiet:Accent,$"<b>{seat+1:00} /</b>")+"  "+Paint(Ink,"<b>"+rows[0]+"</b>");
        for(int i=1;i<rows.Length;i++)
        {
            var r=rows[i];
            if(r.StartsWith("收 ",StringComparison.Ordinal)||r.StartsWith("发 ",StringComparison.Ordinal))
            {
                var value=r[2..]; bool known=value!="未知";
                rows[i]=Paint(Quiet,$"<size=86%>{(r[0]=='收'?"RX":"TX")}  {r[0]}</size>")+"   "+
                    Paint(known?Ink:Quiet,known?"<b>"+value+"</b>":value);
            }
            else if(bot || r.StartsWith("采样",StringComparison.Ordinal) || r.Contains("--") || i>=3)
                rows[i]=Paint(Quiet,"<size=90%>"+r+"</size>");
            else rows[i]=Paint(Ink,r);
        }
        return string.Join("\n",rows);
    }
}
