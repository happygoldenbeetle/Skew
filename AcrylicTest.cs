using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using WinRT;

namespace Mori
{
    public class AcrylicTest
    {
        public void Test(Window window)
        {
            var c = new DesktopAcrylicController();
            var target = window.As<Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop>();
            c.AddSystemBackdropTarget(target);
        }
    }
}
