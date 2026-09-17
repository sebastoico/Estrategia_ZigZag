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
	// Estrategia que usa un ZigZag para detectar pivotes y dibujar áreas de soporte/resistencia
	// sobre el gráfico. La idea principal es resaltar zonas relevantes de pivotes al alza y a la baja.
	public class Estrategia_ZigZag : Strategy
	{
		// Referencia al indicador ZigZag que identifica máximos y mínimos.
		private ZigZag zigZag;

		// Guarda qué pivotes ya fueron dibujados para evitar duplicados en la misma barra.
		private readonly HashSet<int> pivotesDibujados = new HashSet<int>();

		// Almacena las áreas de pivote que se están dibujando en el gráfico.
		private readonly List<AreaPivote> areasPivote = new List<AreaPivote>();

		// Fecha del último reinicio de dibujo para limpiar cada día.
		private DateTime ultimaFechaDeDibujo;

		// Parámetro configurado desde la interfaz del indicador/estrategia.
		[NinjaScriptProperty]
		[Display(Name = "Hora inicio dibujo", Order = 1, GroupName = "Parámetros")]
		public TimeSpan HoraInicioDibujo { get; set; }

		// Parámetro configurado desde la interfaz del indicador/estrategia.
		[NinjaScriptProperty]
		[Display(Name = "Hora fin dibujo", Order = 2, GroupName = "Parámetros")]
		public TimeSpan HoraFinDibujo { get; set; }

		// Estructura interna que describe el rango de un pivote y su zona visual asociada.
		private class AreaPivote
		{
			public int IndicePivote;      // Índice del bar donde se detectó el pivote.
			public int IndiceVelaInicial; // Primer bar del rango visual del área.
			public int IndiceVelaFinal;   // Último bar del rango visual del área.
			public double PrecioSuperior; // Límite superior de la zona del pivote.
			public double PrecioInferior; // Límite inferior de la zona del pivote.
			public bool EsPivoteAlcista; // Indica si el pivote es máximo (alcista) o mínimo (bajista).
		}

		// Este método se ejecuta cada vez que cambia el estado del indicador/estrategia.
		// Aquí se configuran propiedades iniciales, default values y se crea el indicador ZigZag.
		protected override void OnStateChange()
		{
			// Cuando la estrategia se inicializa o se reinicia, limpia los valores de trabajo previos.
			if (State == State.Configure)
			{
				pivotesDibujados.Clear();
				areasPivote.Clear();
			}
			else if (State == State.SetDefaults)
			{
				// Nombre y descripción visibles en NinjaTrader.
				Name = "Estrategia_ZigZag";
				Description = "Estrategia basada en el indicador ZigZag y acción del precio.";

				// Configuración de ejecución: calcula al cierre de la vela y usa 1 entrada por dirección.
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

				// Horarios por defecto en los que se permite dibujar zonas de pivote.
				HoraInicioDibujo = new TimeSpan(8, 30, 0);
				HoraFinDibujo = new TimeSpan(16, 0, 0);
			}
			else if (State == State.DataLoaded)
			{
				// Crea el indicador ZigZag y lo añade al gráfico para tenerlo visible junto con la estrategia.
				zigZag = ZigZag(DeviationType.Points, 1, true);
				AddChartIndicator(zigZag);
			}
		}

		// Se ejecuta en cada nueva vela/barra del mercado.
		// Aquí se valida el horario y se decide si se debe dibujar la zona del pivote actual.
		protected override void OnBarUpdate()
		{
			// Cada nuevo día se reinicia el historial de pivotes dibujados para no mezclar información de sesiones distintas.
			if (CurrentBar == 0)
				ultimaFechaDeDibujo = Time[0].Date;
			else if (Time[0].Date != ultimaFechaDeDibujo.Date)
			{
				pivotesDibujados.Clear();
				areasPivote.Clear();
				ultimaFechaDeDibujo = Time[0].Date;
			}

			// Evita operar antes de tener suficientes barras cargadas.
			if (CurrentBar < BarsRequiredToTrade)
				return;

			// Si la hora actual no está dentro del rango permitido para dibujar, no hace nada.
			if (!EstaDentroDeVentanaDeDibujo(Time[0].TimeOfDay))
				return;

			// Dibuja el último pivote detectado en la barra actual.
			DibujarUltimoPivote();
		}

		// Comprueba si la hora actual cae dentro de la ventana de dibujo indicada.
		// Si HoraInicioDibujo > HoraFinDibujo, considera un rango que cruza el mediodía/noche.
		private bool EstaDentroDeVentanaDeDibujo(TimeSpan horaActual)
		{
			if (HoraInicioDibujo <= HoraFinDibujo)
				return horaActual >= HoraInicioDibujo && horaActual <= HoraFinDibujo;

			return horaActual >= HoraInicioDibujo || horaActual <= HoraFinDibujo;
		}

		// Consulta el último máximo o mínimo del ZigZag y decide si debe dibujar un área asociada.
		private void DibujarUltimoPivote()
		{
			// HighBar(0, 1, CurrentBar) devuelve cuántas barras atrás está el último máximo detectado.
			// LowBar(0, 1, CurrentBar) devuelve cuántas barras atrás está el último mínimo detectado.
			int barrasDesdeMaximo = zigZag.HighBar(0, 1, CurrentBar);
			int barrasDesdeMinimo = zigZag.LowBar(0, 1, CurrentBar);

			// Si cualquiera de los dos valores es negativo, significa que aún no hay pivote válido en ese sentido.
			// El comentario indica que el pivote más reciente sigue en formación, mientras que el anterior es el confirmado.
			if (barrasDesdeMaximo < 0 || barrasDesdeMinimo < 0)
				return;

			// Se comprueba que el pivote haya ocurrido dentro del horario permitido para dibujar.
			if (!EstaDentroDeVentanaDeDibujo(Time[Math.Max(barrasDesdeMaximo, barrasDesdeMinimo)].TimeOfDay))
				return;

			// Si el último máximo está más lejos que el mínimo, significa que el pivote dominante es un máximo.
			if (barrasDesdeMaximo > barrasDesdeMinimo)
				DibujarAreaPivote(barrasDesdeMaximo, true);
			else
				DibujarAreaPivote(barrasDesdeMinimo, false);
		}

		// Crea o actualiza el área visual del pivote en función de la dirección del movimiento y su rango de precio.
		private void DibujarAreaPivote(int barrasAgo, bool esMaximo)
		{
			// El pivote actual se ubica en la barra 'CurrentBar - barrasAgo'.
			int indicePivote = CurrentBar - barrasAgo;

			// Evita dibujar el mismo pivote más de una vez.
			if (!pivotesDibujados.Add(indicePivote))
				return;

			// El cuerpo y el extremo de la vela ayudan a definir la zona superior/inferior del área.
			double precioCuerpo = esMaximo
				? Math.Max(Open[barrasAgo], Close[barrasAgo])
				: Math.Min(Open[barrasAgo], Close[barrasAgo]);
			double precioExtremo = esMaximo ? High[barrasAgo] : Low[barrasAgo];
			double precioSuperior = esMaximo ? precioExtremo : precioCuerpo;
			double precioInferior = esMaximo ? precioCuerpo : precioExtremo;
			int indiceVelaInicial = indicePivote - 1;

			// Recorre todas las áreas existentes para fusionarlas, expandirlas o eliminar superposiciones.
			for (int indiceArea = areasPivote.Count - 1; indiceArea >= 0; indiceArea--)
			{
				AreaPivote area = areasPivote[indiceArea];

				// Caso 1: la nueva zona queda completamente dentro de un área previa.
				bool nuevaAreaDentroDeAnterior = precioSuperior <= area.PrecioSuperior &&
					precioInferior >= area.PrecioInferior;
				if (nuevaAreaDentroDeAnterior)
				{
					// Amplía el rango temporal del área anterior y la redibuja.
					area.IndiceVelaFinal = Math.Max(area.IndiceVelaFinal, indicePivote + 1);
					RedibujarAreaPivote(area);
					return;
				}

				// Caso 2: la nueva zona engloba totalmente a una zona previa.
				bool nuevaAreaContieneAnterior = precioSuperior >= area.PrecioSuperior &&
					precioInferior <= area.PrecioInferior;
				if (nuevaAreaContieneAnterior)
				{
					// La zona anterior queda absorbida; se fusiona el rango de barras y se elimina el dibujo viejo.
					indiceVelaInicial = Math.Min(indiceVelaInicial, area.IndiceVelaInicial);
					RemoveDrawObject((area.EsPivoteAlcista ? "PivoteAlcista_" : "PivoteBajista_") + area.IndicePivote);
					areasPivote.RemoveAt(indiceArea);
				}
			}

			// Busca si el pivote nuevo está dentro de un área anterior y, en ese caso, la expande.
			AreaPivote areaAnterior = areasPivote.FindLast(area =>
				(precioCuerpo >= area.PrecioInferior && precioCuerpo <= area.PrecioSuperior) ||
				(precioExtremo >= area.PrecioInferior && precioExtremo <= area.PrecioSuperior));
			if (areaAnterior != null)
			{
				// Actualiza la duración de la zona afectada para incluir el nuevo pivote.
				areaAnterior.IndiceVelaFinal = Math.Max(areaAnterior.IndiceVelaFinal, indicePivote + 1);
				bool cuerpoDentroDelArea = precioCuerpo >= areaAnterior.PrecioInferior &&
					precioCuerpo <= areaAnterior.PrecioSuperior;

				// Si el cuerpo pertenece a la misma clase de pivote, ajusta los límites de la zona.
				if (cuerpoDentroDelArea && areaAnterior.EsPivoteAlcista == esMaximo)
				{
					if (esMaximo && precioExtremo > areaAnterior.PrecioSuperior)
						areaAnterior.PrecioSuperior = precioExtremo;
					else if (!esMaximo && precioExtremo < areaAnterior.PrecioInferior)
						areaAnterior.PrecioInferior = precioExtremo;
				}

				RedibujarAreaPivote(areaAnterior);
				return;
			}

			// Si ninguna regla anterior aplica, crea una nueva zona visual para el pivote.
			AreaPivote areaNueva = new AreaPivote
			{
				IndicePivote = indicePivote,
				IndiceVelaInicial = indiceVelaInicial,
				IndiceVelaFinal = indicePivote + 1,
				PrecioSuperior = precioSuperior,
				PrecioInferior = precioInferior,
				EsPivoteAlcista = esMaximo
			};
			areasPivote.Add(areaNueva);
			RedibujarAreaPivote(areaNueva);
		}

		// Dibuja en el gráfico un rectángulo que representa el rango del pivote.
		private void RedibujarAreaPivote(AreaPivote area)
		{
			// El rectángulo se dibuja desde la vela inicial hasta la final, en el precio superior/inferior.
			int barrasIniciales = CurrentBar - area.IndiceVelaInicial;
			int barrasFinales = CurrentBar - area.IndiceVelaFinal;
			string etiqueta = (area.EsPivoteAlcista ? "PivoteAlcista_" : "PivoteBajista_") + area.IndicePivote;
			Brush color = area.EsPivoteAlcista ? Brushes.LimeGreen : Brushes.IndianRed;

			// Draw.Rectangle genera un bloque visual sobre el chart para resaltar la zona del pivote.
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
