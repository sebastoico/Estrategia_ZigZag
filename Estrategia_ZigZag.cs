using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using NinjaTrader.Cbi;
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
		private int cantidadAreasDelDia;
		private double ultimoPivoteMaximo;
		private double ultimoPivoteMinimo;
		private double cierreAnterior;
		private Order ordenEntradaPendiente;
		private int barraDeEntradaPendiente = -1;
		private string nombreEntradaPendiente;
		// Solo se permite una entrada pendiente; esta etiqueta identifica su dibujo para poder retirarlo.
		private string etiquetaRiskRewardPendiente;

		// Parámetro configurado desde la interfaz del indicador/estrategia.
		[NinjaScriptProperty]
		[Display(Name = "Hora inicio dibujo", Order = 1, GroupName = "Parámetros")]
		public TimeSpan HoraInicioDibujo { get; set; }

		// Parámetro configurado desde la interfaz del indicador/estrategia.
		[NinjaScriptProperty]
		[Display(Name = "Hora fin dibujo", Order = 2, GroupName = "Parámetros")]
		public TimeSpan HoraFinDibujo { get; set; }

		[NinjaScriptProperty]
		[Range(0.1, double.MaxValue)]
		[Display(Name = "Relación take profit", Order = 3, GroupName = "Parámetros")]
		public double RelacionTakeProfit { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Velas para cancelar orden", Order = 4, GroupName = "Parámetros")]
		public int VelasParaCancelarOrden { get; set; }

		[NinjaScriptProperty]
		[Range(0.01, 100.0)]
		[Display(Name = "% de la cuenta por operación", Order = 5, GroupName = "Parámetros")]
		public double PorcentajeCuentaPorOperacion { get; set; }

		// Estructura interna que describe el rango de un pivote y su zona visual asociada.
		private class AreaPivote
		{
			public int IndicePivote;      // Índice del bar donde se detectó el pivote.
			public int IndiceVelaInicial; // Primer bar del rango visual del área.
			public int IndiceVelaFinal;   // Último bar del rango visual del área.
			public double PrecioSuperior; // Límite superior de la zona del pivote.
			public double PrecioInferior; // Límite inferior de la zona del pivote.
			public bool EsPivoteAlcista; // Indica si el pivote es máximo (alcista) o mínimo (bajista).
			public bool VioPivotePorEncima; // Indica si apareció un pivote por encima del área.
			public bool VioPivotePorDebajo; // Indica si apareció un pivote por debajo del área.
			public bool EstaInvalidada; // Indica si el área dejó de estar activa.
			public bool EsAreaCompra;
			public bool TieneClasificacion;
			public int IndiceBarraCreacion;
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
				cantidadAreasDelDia = 0;
				ultimoPivoteMaximo = 0;
				ultimoPivoteMinimo = 0;
				cierreAnterior = 0;
				ordenEntradaPendiente = null;
				barraDeEntradaPendiente = -1;
				nombreEntradaPendiente = null;
				etiquetaRiskRewardPendiente = null;
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
				RelacionTakeProfit = 1.0;
				VelasParaCancelarOrden = 5;
				PorcentajeCuentaPorOperacion = 1.0;
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
				cantidadAreasDelDia = 0;
				ultimoPivoteMaximo = 0;
				ultimoPivoteMinimo = 0;
				CancelarOrdenPendiente();
				ultimaFechaDeDibujo = Time[0].Date;
			}

			// Evita operar antes de tener suficientes barras cargadas.
			if (CurrentBar < BarsRequiredToTrade)
				return;

			// El cruce se evalúa antes de detectar el nuevo pivote para no usar una zona recién extendida.
			GestionarCrucesDeAreas();
			GestionarCaducidadDeOrden();

			// La ventana limita la creación de zonas, pero no la caducidad de órdenes pendientes.
			if (EstaDentroDeVentanaDeDibujo(Time[0].TimeOfDay))
				DibujarUltimoPivote();

			cierreAnterior = Close[0];
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
			if (esMaximo)
				ultimoPivoteMaximo = precioExtremo;
			else
				ultimoPivoteMinimo = precioExtremo;

			// Recorre todas las áreas existentes para fusionarlas, expandirlas o eliminar superposiciones.
			for (int indiceArea = areasPivote.Count - 1; indiceArea >= 0; indiceArea--)
			{
				AreaPivote area = areasPivote[indiceArea];
				bool pivotePorEncima = precioExtremo > area.PrecioSuperior;
				bool pivotePorDebajo = precioExtremo < area.PrecioInferior;
				bool pivoteExtiendeArea =
					(precioCuerpo >= area.PrecioInferior && precioCuerpo <= area.PrecioSuperior) ||
					(precioExtremo >= area.PrecioInferior && precioExtremo <= area.PrecioSuperior);

				// Un máximo se invalida con la secuencia arriba y después abajo.
				// Un mínimo se invalida con la secuencia abajo y después arriba.
				if (!pivoteExtiendeArea && area.EsPivoteAlcista)
				{
					if (pivotePorEncima)
						area.VioPivotePorEncima = true;
					else if (pivotePorDebajo && area.VioPivotePorEncima)
					{
						area.EstaInvalidada = true;
						RedibujarAreaPivote(area);
						areasPivote.RemoveAt(indiceArea);
						continue;
					}
				}
				else if (!pivoteExtiendeArea)
				{
					if (pivotePorDebajo)
						area.VioPivotePorDebajo = true;
					else if (pivotePorEncima && area.VioPivotePorDebajo)
					{
						area.EstaInvalidada = true;
						RedibujarAreaPivote(area);
						areasPivote.RemoveAt(indiceArea);
						continue;
					}
				}

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
				EsPivoteAlcista = esMaximo,
				IndiceBarraCreacion = CurrentBar
			};
			cantidadAreasDelDia++;
			if (cantidadAreasDelDia <= 2)
				areaNueva.EsAreaCompra = esMaximo;
			else
				areaNueva.EsAreaCompra = precioInferior > Close[0];
			areaNueva.TieneClasificacion = cantidadAreasDelDia <= 2 ||
				precioInferior > Close[0] || precioSuperior < Close[0];
			areasPivote.Add(areaNueva);
			RedibujarAreaPivote(areaNueva);
		}

		private void GestionarCrucesDeAreas()
		{
			if (!EstaDentroDeVentanaDeDibujo(Time[0].TimeOfDay))
			{
				CancelarOrdenPendiente();
				return;
			}

			if (CurrentBar == 0 || cierreAnterior == 0)
				return;

			for (int indiceArea = areasPivote.Count - 1; indiceArea >= 0; indiceArea--)
			{
				AreaPivote area = areasPivote[indiceArea];
				if (!area.TieneClasificacion || area.IndiceBarraCreacion == CurrentBar)
					continue;

				bool cruzaCompraAVenta = area.EsAreaCompra && cierreAnterior <= area.PrecioSuperior && Close[0] > area.PrecioSuperior;
				bool cruzaVentaACompra = !area.EsAreaCompra && cierreAnterior >= area.PrecioInferior && Close[0] < area.PrecioInferior;

				if (cruzaCompraAVenta)
				{
					area.EsAreaCompra = false;
					EnviarOrdenDeCompra();
				}
				else if (cruzaVentaACompra)
				{
					area.EsAreaCompra = true;
					EnviarOrdenDeVenta();
				}
			}
		}

		private void EnviarOrdenDeCompra()
		{
			if (!EstaDentroDeVentanaDeDibujo(Time[0].TimeOfDay))
				return;

			double precioStop;
			// El stop usa el mínimo más reciente que ZigZag todavía tiene pendiente de confirmar.
			if (nombreEntradaPendiente != null || !ObtenerUltimoPivotePorConfirmar(false, out precioStop))
				return;

			double precioEntrada = High[0];
			double riesgo = precioEntrada - precioStop;
			if (riesgo <= 0)
				return;
			double precioTarget = precioEntrada + riesgo * RelacionTakeProfit;
			// Una zona activa entre la entrada y el target puede bloquear el recorrido del precio.
			if (TargetAtraviesaZonaActiva(precioEntrada, precioTarget))
				return;

			int cantidad = CalcularCantidadContratos(precioEntrada, precioStop);
			if (cantidad <= 0)
				return;

			string nombre = "Compra_" + CurrentBar;
			SetStopLoss(nombre, CalculationMode.Price, precioStop, false);
			SetProfitTarget(nombre, CalculationMode.Price, precioTarget);
			DibujarRiskReward(nombre, precioEntrada, precioStop);
			nombreEntradaPendiente = nombre;
			barraDeEntradaPendiente = CurrentBar;
			EnterLongStopMarket(0, true, cantidad, precioEntrada, nombre);
		}

		private void EnviarOrdenDeVenta()
		{
			if (!EstaDentroDeVentanaDeDibujo(Time[0].TimeOfDay))
				return;

			double precioStop;
			// El stop usa el máximo más reciente que ZigZag todavía tiene pendiente de confirmar.
			if (nombreEntradaPendiente != null || !ObtenerUltimoPivotePorConfirmar(true, out precioStop))
				return;

			double precioEntrada = Low[0];
			double riesgo = precioStop - precioEntrada;
			if (riesgo <= 0)
				return;
			double precioTarget = precioEntrada - riesgo * RelacionTakeProfit;
			// Una zona activa entre la entrada y el target puede bloquear el recorrido del precio.
			if (TargetAtraviesaZonaActiva(precioEntrada, precioTarget))
				return;

			int cantidad = CalcularCantidadContratos(precioEntrada, precioStop);
			if (cantidad <= 0)
				return;

			string nombre = "Venta_" + CurrentBar;
			SetStopLoss(nombre, CalculationMode.Price, precioStop, false);
			SetProfitTarget(nombre, CalculationMode.Price, precioTarget);
			DibujarRiskReward(nombre, precioEntrada, precioStop);
			nombreEntradaPendiente = nombre;
			barraDeEntradaPendiente = CurrentBar;
			EnterShortStopMarket(0, true, cantidad, precioEntrada, nombre);
		}

		private int CalcularCantidadContratos(double precioEntrada, double precioStop)
		{
			if (Instrument == null || Account == null || PorcentajeCuentaPorOperacion <= 0)
				return DefaultQuantity;

			Currency monedaCuenta = Instrument.MasterInstrument.Currency;
			double valorCuenta = Account.Get(AccountItem.NetLiquidation, monedaCuenta);
			if (valorCuenta <= 0)
				valorCuenta = Account.Get(AccountItem.CashValue, monedaCuenta);

			double riesgoEnDolares = Math.Abs(precioEntrada - precioStop) * Instrument.MasterInstrument.PointValue;
			if (valorCuenta <= 0 || riesgoEnDolares <= 0)
				return DefaultQuantity;

			double riesgoObjetivo = valorCuenta * (PorcentajeCuentaPorOperacion / 100.0);
			double contratos = riesgoObjetivo / riesgoEnDolares;
			return Math.Max(1, (int)Math.Floor(contratos));
		}

		private bool TargetAtraviesaZonaActiva(double precioEntrada, double precioTarget)
		{
			// Se comprueba la intersección con el segmento completo, no solo con el precio final.
			double recorridoSuperior = Math.Max(precioEntrada, precioTarget);
			double recorridoInferior = Math.Min(precioEntrada, precioTarget);

			foreach (AreaPivote area in areasPivote)
			{
				if (area.EstaInvalidada || !area.TieneClasificacion)
					continue;

				bool zonaEnRecorrido = area.PrecioInferior <= recorridoSuperior &&
					area.PrecioSuperior >= recorridoInferior;
				if (zonaEnRecorrido)
					return true;
			}

			return false;
		}

		private bool ObtenerUltimoPivotePorConfirmar(bool esMaximo, out double precioStop)
		{
			// HighBar/LowBar devuelve la distancia al extremo actual del ZigZag; el margen de un tick
			// coloca el stop detrás del pivote y evita dejarlo exactamente sobre el extremo.
			int barrasAgo = esMaximo
				? zigZag.HighBar(0, 1, CurrentBar)
				: zigZag.LowBar(0, 1, CurrentBar);
			if (barrasAgo < 0)
			{
				precioStop = 0;
				return false;
			}

			double precioPivote = esMaximo ? High[barrasAgo] : Low[barrasAgo];
			precioStop = esMaximo
				? Instrument.MasterInstrument.RoundToTickSize(precioPivote + TickSize)
				: Instrument.MasterInstrument.RoundToTickSize(precioPivote - TickSize);
			return precioStop > 0;
		}

		private void GestionarCaducidadDeOrden()
		{
			// Se usa el nombre de señal y no la referencia Order porque el callback puede llegar después.
			if (nombreEntradaPendiente != null &&
				CurrentBar - barraDeEntradaPendiente >= VelasParaCancelarOrden)
				CancelarOrdenPendiente();
		}

		private void CancelarOrdenPendiente()
		{
			// La cancelación elimina también la representación visual de una orden que no se llenó.
			if (ordenEntradaPendiente != null &&
				(ordenEntradaPendiente.OrderState == OrderState.Working || ordenEntradaPendiente.OrderState == OrderState.Accepted ||
				 ordenEntradaPendiente.OrderState == OrderState.Submitted))
				CancelOrder(ordenEntradaPendiente);
			if (etiquetaRiskRewardPendiente != null)
				RemoveDrawObject(etiquetaRiskRewardPendiente);
			ordenEntradaPendiente = null;
			barraDeEntradaPendiente = -1;
			nombreEntradaPendiente = null;
			etiquetaRiskRewardPendiente = null;
		}

		private void DibujarRiskReward(string nombreEntrada, double precioEntrada, double precioStop)
		{
			// RiskReward calcula el target a partir del stop y de la relación configurada.
			etiquetaRiskRewardPendiente = "RiskReward_" + nombreEntrada + "_" + CurrentBar;
			Draw.RiskReward(
				this,
				etiquetaRiskRewardPendiente,
				false,
				0,
				precioEntrada,
				-VelasParaCancelarOrden,
				precioStop,
				RelacionTakeProfit,
				true,
				false,
				string.Empty);
		}

		protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice, int quantity, int filled,
			double averageFillPrice, OrderState orderState, DateTime time, ErrorCode error, string comment)
		{
			if (nombreEntradaPendiente == null || order.Name != nombreEntradaPendiente)
				return;

			if (orderState == OrderState.Submitted || orderState == OrderState.Accepted || orderState == OrderState.Working)
				ordenEntradaPendiente = order;
			else if (orderState == OrderState.Filled || orderState == OrderState.Cancelled || orderState == OrderState.Rejected)
			{
				ordenEntradaPendiente = null;
				barraDeEntradaPendiente = -1;
				nombreEntradaPendiente = null;
				if (orderState == OrderState.Cancelled || orderState == OrderState.Rejected)
				{
					if (etiquetaRiskRewardPendiente != null)
						RemoveDrawObject(etiquetaRiskRewardPendiente);
					etiquetaRiskRewardPendiente = null;
				}
			}
		}

		// Dibuja en el gráfico un rectángulo que representa el rango del pivote.
		private void RedibujarAreaPivote(AreaPivote area)
		{
			// El rectángulo se dibuja desde la vela inicial hasta la final, en el precio superior/inferior.
			int barrasIniciales = CurrentBar - area.IndiceVelaInicial;
			int barrasFinales = CurrentBar - area.IndiceVelaFinal;
			string etiqueta = (area.EsPivoteAlcista ? "PivoteAlcista_" : "PivoteBajista_") + area.IndicePivote;
			Brush color = area.EstaInvalidada
				? Brushes.Gray
				: area.EsPivoteAlcista ? Brushes.LimeGreen : Brushes.IndianRed;

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
