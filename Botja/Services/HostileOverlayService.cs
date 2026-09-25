using Ocelot.Graphics;
using Ocelot.Lifecycle;
using Ocelot.Services.OverlayRenderer;
using Ocelot.Windows;

namespace Botja.Services;

public sealed class HostileOverlayService(
    HostileDetectionService hostileDetection,
    IOverlayRenderer overlay,
    IMainWindow mainWindow
) : IOnRender
{
    public void Render()
    {
        if (!mainWindow.IsOpen)
            return;

        foreach (var hostile in hostileDetection.GetNearbyHostiles())
            overlay.StrokeCircle(hostile.Position, 2f, new Color(1f, 0f, 0f, 0.8f));
    }
}
