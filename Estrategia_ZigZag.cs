using System;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.Strategies;

namespace NinjaTrader.NinjaScript.Strategies
{
	public class Estrategia_ZigZag : Strategy
	{
		private ZigZag zigZag;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Name = "Estrategia_ZigZag";
				Description = "Estrategia basada en el indicador ZigZag y acción del precio.";
				Calculate = Calculate.OnBarClose;
				EntriesPerDirection = 1;
				EntryHandling = EntryHandling.AllEntries;
				IsExitOnSessionCloseStrategy = true;
				ExitOnSessionCloseSeconds = 30;
				IsFillLimitOnTouch = false;
				MaximumBarsLookBack = MaximumBarsLookBack.TwoHundredFiftySix;
				OrderFillResolution = OrderFillResolution.Standard;
				Slippage = 0;
				StartBehavior = StartBehavior.WaitUntilFlat;
				TimeInForce = NinjaTrader.Cbi.TimeInForce.Gtc;
				TraceOrders = false;
				RealtimeErrorHandling = RealtimeErrorHandling.StopCancelClose;
				StopTargetHandling = StopTargetHandling.PerEntryExecution;
				BarsRequiredToTrade = 20;

			}
			else if (State == State.DataLoaded)
			{
				zigZag = ZigZag(DeviationType.Points, 0, true);
				AddChartIndicator(zigZag);
			}
		}

		protected override void OnBarUpdate()
		{
			if (CurrentBar < BarsRequiredToTrade)
				return;

			// Aqui se agregara la logica de entradas y salidas.
		}

	}
}
