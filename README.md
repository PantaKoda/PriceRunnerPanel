# PriceRunner Panel

A small Windows panel for waiting out a good price. Search pricerunner.se, pin the products you want, and the
panel checks their prices on a schedule and pops a Windows notification when one drops.

Built with .NET 10 and Avalonia 12, in the same "HUD" visual language as Repo Watch, but light themed.

## Use it

```
dotnet run --project src/PriceRunnerPanel
```

or build a single exe with `pwsh scripts/publish.ps1` (output in `artifacts/publish`).

- **Search** — type a product name; results (picture, stores, rating, lowest price, recent drop) appear as you type.
  **PIN** starts tracking; click again to unpin. Click a name to open it on PriceRunner.
- **Watchlist** — every pinned product shows its current lowest price, the change since you pinned it, its price
  history, and a meter of where today's price sits between the lowest and highest seen. The card glows green at
  the lowest price seen.
- **Target price** — type an amount (Enter or leave the field) to be alerted when the price is at or below it.
- **Check every** 15 min – 24 h, or **Check now** (F5). Products are checked one after another with a short pause.
- **Notifications** for a drop of at least 1 %, a new lowest price, or a reached target. Clicking one opens the
  product on PriceRunner. Windows' notification settings and Do not disturb apply.
- Closing the window hides it to the tray so tracking continues. The tray icon toggles the window; its menu has
  *Check prices now*, *Start with Windows* and *Quit*. Starting the app again shows the running window.
- **Start with Windows** is on by default: the app registers itself in the per-user Run key
  (`HKCU\…\CurrentVersion\Run`, value `PriceRunnerPanel`) and starts hidden in the tray at sign-in. The entry points
  at whichever exe ran last, so run the published exe once after building. Turn it off from the tray menu or
  Task Manager › Startup apps.

## How prices are read

Two public JSON endpoints that the pricerunner.se web app itself uses, anonymously and read-only:

- search: `/se/api/search-edge-rest/public/search/v6/SE?q=…`
- offers of one product: `/se/api/product-detail-edge-rest/public/product-detail/v0/offers/SE/{id}` filtered to new
  items from Swedish stores.

The tracked price is the cheapest **in-stock, new** offer (falling back to the cheapest offer when nothing is in
stock), which matches the price PriceRunner shows. The endpoints are undocumented and may change; a failed check
shows its error on the card and the next check tries again.

## Data

Everything lives in `%LOCALAPPDATA%\PriceRunnerPanel`: `state.json` (pins, history, settings) and an `images`
thumbnail cache. Set `PRICERUNNERPANEL_DATA` to use another folder (handy for testing).
