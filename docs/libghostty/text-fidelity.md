# Text and cursor fidelity

Terminal frames retain underline style and resolved underline color, overline, blink, hollow cursor shape, wide-tail position, and password-input metadata. Password-input metadata is informational and does not establish whether terminal text is secret. The renderer uses the active terminal palette when capturing underline colors. Text blink follows the existing animation preference and timer. Hidden text does not start the timer.
