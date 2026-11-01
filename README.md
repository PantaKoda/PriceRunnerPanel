# PriceRunner Panel

A small Windows panel for waiting out a good price. Search pricerunner.se, pin the products you want, and the
panel checks their prices on a schedule and pops a Windows notification when one drops.

Built with .NET 10 and Avalonia 12, in the same "HUD" visual language as Repo Watch, but light themed.

> Independent hobby project. Not affiliated with, endorsed by or connected to PriceRunner or Klarna. It reads the
> same public data the pricerunner.se website shows, politely (one product at a time, on a schedule you choose).

## Install

1. Download `PriceRunnerPanel-X.Y.Z-win-x64.zip` from [Releases](https://github.com/PantaKoda/PriceRunnerPanel/releases/latest).
2. Extract it to `%LOCALAPPDATA%\Programs` (you get `%LOCALAPPDATA%\Programs\PriceRunnerPanel`).
3. Start `PriceRunnerPanel.exe`. No .NET install needed. The app isn't code-signed, so SmartScreen may ask once.
   It adds itself to the Start menu, so from then on you can find it in Windows search.

To uninstall, quit the app, then delete that folder, `%LOCALAPPDATA%\PriceRunnerPanel` and the *PriceRunner Panel*
Start menu shortcut.

From then on the app updates itself: see [Updates](#updates).

## Build from source

```
dotnet run --project src/PriceRunnerPanel
```

A copy built from source works fully but never replaces itself; the update window links to GitHub instead.

- **Search** — type a product name; results (picture, stores, rating, lowest price, recent drop) appear as you type.
  **PIN** starts tracking; click again to unpin. Click a name to open it on PriceRunner.
- **Black Friday** — PriceRunner's current deals, the list on
  [pricerunner.se/c/black-friday](https://www.pricerunner.se/c/black-friday): products at least 10 % below their
  earlier price, newest drops first. Filter by category and store (popular ones first, type to find the rest, tick
  several). Once a filter is on, a separate *Refine these results* box shows the top brands and stores in the results,
  to narrow by brand, store or both. What you picked shows as chips with a cross to remove them. With a store filter on, each listing also
  shows the picked store's own price, fetched one product at a time, since the main price is the lowest at any store.
  **PIN** adds a deal to the watchlist, whose verdict shows whether the discount is real. Deals are fetched
  when the tab opens and aren't saved; their thumbnails are cleared at the next start.
- **Watchlist** — every pinned product shows its current lowest price, the change since you pinned it, its price
  history, and a meter of where today's price sits between the lowest and highest seen. The card glows green at
  the lowest price seen.
- **Real deal or fake discount?** Each card compares today's price with the product's *typical* price: the median of
  PriceRunner's daily lowest price from 3 to 11 weeks ago. The last three weeks are left out, so a price raised
  before a sale (the Black Friday trick) doesn't become the yardstick. The verdict chip says **REAL DEAL** (clearly
  below typical and near the year's low, or 12 % or more below typical), **GOOD PRICE** (5 % below), **TYPICAL
  PRICE**, **ABOVE TYPICAL** (5 % above) or **⚠ INFLATED DROP**: the price was raised by 5 % or more in the last
  month and is coming down again without getting cheaper than before. Inflated drops don't notify. The card also
  shows the typical price and the year's lowest price with its store and date, and its chart and meter cover the
  year.
- **Variants are grouped** — pin several colours of one product and they share a box headed by the product's
  name and its cheapest colour; each card shows its colour. Grouping uses PriceRunner's own variant groups, and a
  single store's listing (e.g. "Sony WH-1000XM6 … Sand Pink") joins the box when its name starts with the
  product's name in the same category.
- **Target price** — type an amount (Enter or leave the field) to be alerted when the price is at or below it.
- **Check every** 15 min – 24 h, or **Check now** (F5). Products are checked one after another with a short pause.
- **Notifications** for a drop of at least 1 %, a new lowest price, or a reached target. Clicking one opens the
  product on PriceRunner. Windows' notification settings and Do not disturb apply.
- Closing the window hides it to the tray so tracking continues. The tray icon toggles the window; its menu has
  *Check prices now*, *Check for updates…*, *Start with Windows* and *Quit*. Starting the app again shows the
  running window.
- **Start with Windows** is on by default: the app registers itself in the per-user Run key
  (`HKCU\…\CurrentVersion\Run`, value `PriceRunnerPanel`) and starts hidden in the tray at sign-in. The entry points
  at whichever exe ran last. Turn it off from the tray menu or Task Manager › Startup apps.

## Updates

- The app checks this repository's GitHub releases 20 seconds after it starts and then every 12 hours,
  anonymously. When a newer release exists, an **UPDATE** button appears in the header and a notification is shown
  once for that version. *Check for updates* (bottom-right of the panel, next to your version, or in the tray menu)
  checks right away.
- **Install update** (only when you click it) downloads the release zip from github.com, checks it against the
  release's SHA-256, unpacks it into `%LOCALAPPDATA%\PriceRunnerPanel\updates`, quits, and lets the new copy swap
  itself into the install folder and start. The previous version is kept as `<folder>.previous`; a failed swap
  puts it back and says so. Your watchlist and settings live outside the app folder and are kept.
- See [SECURITY.md](SECURITY.md) for what is and isn't guaranteed.

### Publishing a release (maintainers)

1. Set `<Version>` in `Directory.Build.props` and add a `## [X.Y.Z] - date` section to `CHANGELOG.md` (it becomes
   the release notes the app shows).
2. Commit to `main`, then tag and push: `git tag vX.Y.Z` and `git push origin vX.Y.Z`.
3. The **Release** workflow checks the tag matches the version, builds the zip with `scripts/publish-windows.ps1`,
   attests its provenance and publishes the GitHub release. Running apps offer it within 12 hours.

## How prices are read

Two public JSON endpoints that the pricerunner.se web app itself uses, anonymously and read-only:

- search: `/se/api/search-edge-rest/public/search/v6/SE?q=…`
- offers of one product: `/se/api/product-detail-edge-rest/public/product-detail/v0/offers/SE/{id}` filtered to new
  items from Swedish stores.
- deals and their filters: `/se/api/deals-edge-rest/public/search/deals/products/v3/SE` and `…/deals/facets/SE`, with
  `af_PRICE_DROP=-90_-10`, `af_CATEGORY`, `af_MERCHANT_DEAL` and `af_BRAND`. Only fetched while the Black Friday tab is used.
- price history of one product (the product page's *Prisutveckling* tab):
  `/se/api/product-information-edge-rest/public/pricehistory/product/{id}/SE/DAY?selectedInterval=ONE_YEAR&filter=NATIONAL`,
  one lowest price per day for the past year, fetched at most twice a day per product.

The tracked price is the cheapest **in-stock, new** offer (falling back to the cheapest offer when nothing is in
stock), which matches the price PriceRunner shows. The endpoints are undocumented and may change; a failed check
shows its error on the card and the next check tries again.

## Data

Everything lives in `%LOCALAPPDATA%\PriceRunnerPanel`: `state.json` (pins, history, settings) and an `images`
thumbnail cache. Unpinning a product deletes everything kept about it (its prices, history, target, picture and
notifications); thumbnails of search results are cleared at the next start. Set `PRICERUNNERPANEL_DATA` to use another folder (handy for testing).
