# Venue marks

`../Resources/ExchangeBranding.xaml` exposes reusable `KalshiMark` and
`PolymarketMark` DrawingImages. Trading renders each at 28 DIPs with an 8-DIP
label gap. All artwork is local; there are no runtime downloads or icon packages.

Kalshi uses an original, restrained K identity on a rounded dark-green tile.
It is **not an exact official Kalshi logo**. Its green palette references the
[official brand kit](https://kalshi.com/brandkit); no official wordmark was cropped.

Polymarket uses the official `icon-blue.svg` from the logo pack linked by its
[official brand page](https://polymarket.com/brand):
[polymarket-logos.zip](https://polymarket-upload.s3.us-east-2.amazonaws.com/polymarket-logos.zip).
The original 512×512 blue square, white path, coordinates, fill rule and colors
are preserved in WPF geometry. Only the required icon is represented in the
application resource; the full pack is not shipped.

Marks identify venues. Adjacent Kalshi / USD and Polymarket / USDC text remains
visible and accessible; these marks do not imply endorsement or affiliation.
