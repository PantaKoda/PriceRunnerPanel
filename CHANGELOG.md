# Changelog

What changed in each PriceRunner Panel release. The section for a version becomes its GitHub release notes, which the app shows in its update window, so write for people using the app.

## [0.4.5] - 2026-11-01

### Added
- **Search inside the Black Friday tab:** find text among the deals your filters list, without leaving the tab. Matches appear as they're found. Filters narrow the search, and with a very long list it stops partway and offers *Search more deals*.

## [0.4.4] - 2026-11-01

### Added
- **Filter deals by discount:** type a range (say 30–50 %) or pick 10, 20, 30 or 50 %+ on the Black Friday tab. This is the discount PriceRunner reports.
- **Both views of a deal, side by side:** while a filter is on, each listing also gets the verdict and the typical price and 1-year low, without pinning. Watchlist cards now also show the drop PriceRunner reports next to the verdict.

### Fixed
- **Sale prices were missed.** A store's campaign price (a sale between two dates) was ignored, so watchlist prices could be too high during a sale, and listings could show a store's regular price, like *At Elgiganten: 5 879 kr* for a deal at 3 999 kr. Prices now match what PriceRunner lists.

## [0.4.3] - 2026-11-01

### Added
- **Refine deals by brand and store:** once a filter is on, a separate box on the Black Friday tab shows the brands and stores in the results. Pick brands, stores or both to narrow the list; *More* opens the full list with a search box.

## [0.4.2] - 2026-11-01

### Added
- **See what you picked on the Black Friday tab:** every category and store you tick shows as a chip with a cross to remove it.
- **The store's own price on each deal:** with a store filter on, each listing shows what that store asks, e.g. *At Elgiganten: 5 879 kr*. The big price is still the lowest at any store, which can be a different store.

## [0.4.1] - 2026-11-01

### Added
- **Start menu shortcut:** the app adds itself to the Start menu, so you can find it in Windows search and pin it to the taskbar. If you delete the shortcut, it stays deleted.

## [0.4.0] - 2026-11-01

### Added
- **Black Friday tab:** PriceRunner's current deals (10 % off or more), newest drops first. Filter by **category** and **store**, with the popular ones on top and a search box for the rest; tick several if you like. **PIN** adds a deal to your watchlist, where its verdict shows whether the discount is real. Deals aren't saved.

## [0.3.0] - 2026-11-01

### Added
- **Spot fake discounts.** Each pinned product now pulls PriceRunner's price history for the past year and gets a verdict: *REAL DEAL*, *GOOD PRICE*, *TYPICAL PRICE*, *ABOVE TYPICAL* or *⚠ INFLATED DROP*. The yardstick is the product's typical price (the median 3–11 weeks ago), so a price that was raised just before a sale like Black Friday can't pass itself off as the normal price. A drop that only takes back a recent rise doesn't notify.
- Each card shows the typical price and the lowest price of the year (with the store and date). The history chart, the low/high meter and the "lowest price" alert now cover the whole year, not just the time since you pinned.

### Changed
- **Unpinning deletes everything the app kept about the product:** its price history, target and picture, and its notifications in the Action Center. Pictures from searches are cleared the next time the app starts.

## [0.2.0] - 2026-11-01

### Added
- **Check for updates from the panel itself:** the footer shows your version with a *Check for updates* link (the tray menu still has it too).

### Changed
- When you're up to date, the update window says so clearly instead of showing an empty notes box.

## [0.1.0] - 2026-11-01

### Added
- **Search pricerunner.se** from a light, futuristic panel: pictures, stores, ratings, lowest price and recent drops, as you type.
- **Pin products to track their price.** Each pinned product shows its current lowest price (new, in stock, Swedish stores), the cheapest store, the change since you pinned it, its price history, and where today's price sits between the lowest and highest seen.
- **Notifications** for a price drop of at least 1 %, a new lowest price, or a target price you set.
- **Colours of one product are grouped** in a shared box with the cheapest colour on top of the box.
- **Runs in the tray** and **starts with Windows** (switch it off from the tray menu).
- **Updates inside the app.** The panel checks GitHub for a newer release twice a day. When one exists, an **UPDATE** button appears in the header and you get a notification once. Installing downloads the release, checks it against its published checksum, swaps in the new version and restarts; your watchlist and settings are kept.
