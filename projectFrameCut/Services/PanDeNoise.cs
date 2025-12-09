using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace projectFrameCut
{
    public class PanDeNoise
    {
        // Simple exponential moving average to smooth pan input without introducing a bias offset.
        // Alpha in range (0,1]: smaller alpha -> stronger smoothing (more lag), larger alpha -> more responsive.
        public double Alpha { get; set; } = 0.25;

        private double _smoothed = 0.0;
        private bool _hasValue = false;

        public PanDeNoise() { }

        public void Reset()
        {
            _hasValue = false;
            _smoothed = 0.0;
        }

        public double Process(double input)
        {
            if (!_hasValue)
            {
                _smoothed = input;
                _hasValue = true;
                return _smoothed;
            }

            _smoothed = Alpha * input + (1.0 - Alpha) * _smoothed;
            return _smoothed;
        }
    }
}
