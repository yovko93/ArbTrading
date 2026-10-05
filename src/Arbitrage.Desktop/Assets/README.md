# Application mark

`../Resources/Branding.xaml` is the canonical editable vector source: a blue/green
abstract A with three ascending green market growth bars. `AppMark` presents the
symbol without a tile; `AppIconArtwork` reuses the same geometry on a dark navy
rounded square with transparent outer corners. Icons contain no tiny product
text, keeping the symbol legible at small sizes in both themes and on the taskbar.

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
