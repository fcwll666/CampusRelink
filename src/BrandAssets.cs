using System;
using System.Drawing;
using System.IO;
using System.Reflection;

internal static class BrandAssets
{
    internal static Icon LoadIcon(int size)
    {
        using (Stream source = Assembly.GetExecutingAssembly().GetManifestResourceStream("CampusRelink.AppIcon"))
        {
            if (source == null) throw new InvalidOperationException("CampusRelink icon resource is missing.");
            using (var icon = new Icon(source, new Size(size, size)))
                return (Icon)icon.Clone();
        }
    }
}
