using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;

namespace projectFrameCut.Controls
{
    public class SquareContainer : ContentView
    {
        protected override Size MeasureOverride(double widthConstraint, double heightConstraint)
        {
            // If width is constrained, force height to match
            if (!double.IsInfinity(widthConstraint))
            {
                // Measure content with the square size constraints
                // This ensures the child (Border) knows it has this much space
                base.MeasureOverride(widthConstraint, widthConstraint);
                
                // Return the square size
                return new Size(widthConstraint, widthConstraint);
            }

            return base.MeasureOverride(widthConstraint, heightConstraint);
        }
    }
}
