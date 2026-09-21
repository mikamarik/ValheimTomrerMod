namespace ValheimTomrer.Editor.Input
{
    /// <summary>
    /// A held direction that repeats: once at once, then after <c>delay</c> every <c>every</c>
    /// seconds. Same as the Tomrer editor's Repeater (src/view/gamepad.ts).
    /// </summary>
    internal sealed class Repeater
    {
        private readonly float _delay;
        private readonly float _every;
        private int _value;
        private float _time;

        public Repeater(float delay = 0.4f, float every = 0.09f)
        {
            _delay = delay;
            _every = every;
        }

        /// <summary><c>value</c> 0 is let go. True on the frames the action should happen.</summary>
        public bool Step(int value, float dt)
        {
            if (value != _value)
            {
                _value = value;
                _time = 0f;
                return value != 0;
            }

            if (value == 0)
            {
                return false;
            }

            _time += dt;
            if (_time < _delay)
            {
                return false;
            }

            _time -= _every;
            return true;
        }

        public void Reset()
        {
            _value = 0;
            _time = 0f;
        }
    }
}
