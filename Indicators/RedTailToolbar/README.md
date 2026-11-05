# RedTailToolbar

Replace the existing RedTailToolbar indicator code with `../RedTailToolbar.cs`,
then compile in NinjaTrader 8. Remove the old toolbar from the chart before
adding the updated indicator so its previous layout changes are cleaned up.

The toolbar now uses a row outside the chart tab control rather than inserting
a row into NinjaTrader's internal panel layout. Each chart tab shows only its
own toolbar; inactive toolbar rows collapse.

This addresses the case where removing RedTailToolbar restores the main-panel
bar timer and price labels above the volume-panel divider. Leave BarTimerCustom
on "Same as input series" with its existing text position, and RedTailVolume
on panel 2. No timer-position workaround is required.

NinjaTrader 8 is required to verify the actual chart rendering.
