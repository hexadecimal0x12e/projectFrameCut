using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace projectFrameCut.DraftStuff
{
    public static class TrackCalculator
    {
        public static int HeightPerTrack = 100;

        public static int CalculateWhichTrackShouldIn(double clipPositionY)
        {
            return (int)Math.Round(clipPositionY / HeightPerTrack);
        }
    }
}
