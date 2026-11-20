using TMPro;
using UnityEngine;

namespace TeamModVersions;

internal static class HikariaLoadoutAnchor
{
    // Reference clone: TopLeft text in a 1000-wide watermark rect centered at (1525,-515).
    // Our rect uses a bottom-left pivot, so convert the original top-left before sizing our plate.
    internal static Vector3 Fallback(TextMeshPro template,RectTransform card)
    {
        var t=template.rectTransform; var pivot=t.pivot; var scale=card.localScale;
        return new Vector3(1525-pivot.x*1000*scale.x,-515+((1-pivot.y)*t.rect.height-card.sizeDelta.y)*scale.y,0);
    }
}
