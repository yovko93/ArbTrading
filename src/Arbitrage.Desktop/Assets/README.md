# Application mark

`../Resources/Branding.xaml` is the editable vector source. The blue and green
opposing arrows represent exchange between two markets. The mark contains no
text and uses a fixed navy background so that it remains recognizable in both
application themes and on the Windows taskbar.

`ArbitrageTrading.ico` contains 32-bit PNG frames at 16, 20, 24, 32, 40, 48, 64,
128, and 256 pixels. The Desktop project uses it for the executable icon and
embeds it as a WPF resource for the window icon. No packaging step is required.

After changing the vector source, regenerate the committed icon from the
repository root on Windows:

```powershell
powershell.exe -NoProfile -STA -File scripts/generate-app-icon.ps1
```

The generator uses WPF rendering and writes only the icon in this directory.
It does not build or launch the application. In-app branding can use the
`AppMark` drawing resource; the icon resource URI is
`pack://application:,,,/Arbitrage.Desktop;component/Assets/ArbitrageTrading.ico`.
