using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.Strategies;

namespace NinjaTrader.NinjaScript.Strategies
{
	public class Estrategia_ZigZag : Strategy
	{
		private ZigZag zigZag;
		private readonly HashSet<int> pivotesDibujados = new HashSet<int>();
		private readonly List<AreaPivote> areasPivote = new List<AreaPivote>();
		private DateTime ultimaFechaDeDibujo;

		[NinjaScriptProperty]
		[Display(Name = "Hora inicio dibujo", Order = 1, GroupName = "Parámetros")]
		public TimeSpan HoraInicioDibujo { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Hora fin dibujo", Order = 2, GroupName = "Parámetros")]
		public TimeSpan HoraFinDibujo { get; set; }

		private class AreaPivote
		{
			public int IndicePivote;
			public int IndiceVelaInicial;
			public int IndiceVelaFinal;
			public double PrecioSuperior;
			public double PrecioInferior;
			public bool EsPivoteAlcista;
		}

		protected override void OnStateChange()
		{
			if (State == State.Configure)
			{
				pivotesDibujados.Clear();
				areasPivote.Clear();
			}
			else if (State == State.SetDefaults)
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
				HoraInicioDibujo = new TimeSpan(8, 30, 0);
				HoraFinDibujo = new TimeSpan(16, 0, 0);

			}
			else if (State == State.DataLoaded)
			{
				zigZag = ZigZag(DeviationType.Points, 0, true);
				AddChartIndicator(zigZag);
			}
		}

		protected override void OnBarUpdate()
		{
			if (CurrentBar == 0)
				ultimaFechaDeDibujo = Time[0].Date;
			else if (Time[0].Date != ultimaFechaDeDibujo.Date)
			{
				pivotesDibujados.Clear();
				areasPivote.Clear();
				ultimaFechaDeDibujo = Time[0].Date;
			}

			if (CurrentBar < BarsRequiredToTrade)
				return;
			if (!EstaDentroDeVentanaDeDibujo(Time[0].TimeOfDay))
				return;

			DibujarUltimoPivote();
		}

		private bool EstaDentroDeVentanaDeDibujo(TimeSpan horaActual)
		{
			if (HoraInicioDibujo <= HoraFinDibujo)
				return horaActual >= HoraInicioDibujo && horaActual <= HoraFinDibujo;

			return horaActual >= HoraInicioDibujo || horaActual <= HoraFinDibujo;
		}

		private void DibujarUltimoPivote()
		{
			int barrasDesdeMaximo = zigZag.HighBar(0, 1, CurrentBar);
			int barrasDesdeMinimo = zigZag.LowBar(0, 1, CurrentBar);

			// El pivote mas reciente sigue en formacion; el anterior es el confirmado.
			if (barrasDesdeMaximo < 0 || barrasDesdeMinimo < 0)
				return;
			if (!EstaDentroDeVentanaDeDibujo(Time[Math.Max(barrasDesdeMaximo, barrasDesdeMinimo)].TimeOfDay))
				return;

			if (barrasDesdeMaximo > barrasDesdeMinimo)
				DibujarAreaPivote(barrasDesdeMaximo, true);
			else
				DibujarAreaPivote(barrasDesdeMinimo, false);
		}

		private void DibujarAreaPivote(int barrasAgo, bool esMaximo)
		{
			int indicePivote = CurrentBar - barrasAgo;
			if (!pivotesDibujados.Add(indicePivote))
				return;

			double precioCuerpo = esMaximo
				? Math.Max(Open[barrasAgo], Close[barrasAgo])
				: Math.Min(Open[barrasAgo], Close[barrasAgo]);
			double precioExtremo = esMaximo ? High[barrasAgo] : Low[barrasAgo];
			AreaPivote areaAnterior = areasPivote.Find(area =>
				(precioCuerpo >= area.PrecioInferior && precioCuerpo <= area.PrecioSuperior) ||
				(precioExtremo >= area.PrecioInferior && precioExtremo <= area.PrecioSuperior));
			if (areaAnterior != null)
			{
				areaAnterior.IndiceVelaFinal = Math.Max(areaAnterior.IndiceVelaFinal, indicePivote + 1);
				RedibujarAreaPivote(areaAnterior);
				return;
			}

			areasPivote.Add(new AreaPivote
			{
				IndicePivote = indicePivote,
				IndiceVelaInicial = indicePivote - 1,
				IndiceVelaFinal = indicePivote + 1,
				PrecioSuperior = esMaximo ? precioExtremo : precioCuerpo,
				PrecioInferior = esMaximo ? precioCuerpo : precioExtremo,
				EsPivoteAlcista = esMaximo
			});
			Brush color = esMaximo ? Brushes.IndianRed : Brushes.LimeGreen;
			string etiqueta = (esMaximo ? "PivoteAlcista_" : "PivoteBajista_") + indicePivote;

			Draw.Rectangle(
				this,
				etiqueta,
				true,
				barrasAgo + 1,
				precioExtremo,
				Math.Max(0, barrasAgo - 1),
				precioCuerpo,
				color,
				color,
				20);
		}

		private void RedibujarAreaPivote(AreaPivote area)
		{
			int barrasIniciales = CurrentBar - area.IndiceVelaInicial;
			int barrasFinales = CurrentBar - area.IndiceVelaFinal;
			string etiqueta = (area.EsPivoteAlcista ? "PivoteAlcista_" : "PivoteBajista_") + area.IndicePivote;
			Brush color = area.EsPivoteAlcista ? Brushes.IndianRed : Brushes.LimeGreen;

			Draw.Rectangle(
				this,
				etiqueta,
				true,
				barrasIniciales,
				area.PrecioSuperior,
				Math.Max(0, barrasFinales),
				area.PrecioInferior,
				color,
				color,
				20);
		}

	}
}
