import { useEffect, useRef } from "react";
import { init, use as registerECharts, type EChartsCoreOption } from "echarts/core";
import { BarChart, LineChart } from "echarts/charts";
import {
  DataZoomComponent,
  GridComponent,
  LegendComponent,
  MarkAreaComponent,
  MarkLineComponent,
  TooltipComponent,
} from "echarts/components";
import { CanvasRenderer } from "echarts/renderers";

registerECharts([
  BarChart,
  LineChart,
  DataZoomComponent,
  GridComponent,
  LegendComponent,
  MarkAreaComponent,
  MarkLineComponent,
  TooltipComponent,
  CanvasRenderer,
]);

export type InteractiveChartOption = EChartsCoreOption;

export default function InteractiveChart({
  option,
  ariaLabel,
}: {
  option: InteractiveChartOption;
  ariaLabel: string;
}) {
  const containerRef = useRef<HTMLDivElement>(null);
  const chartRef = useRef<ReturnType<typeof init> | null>(null);

  useEffect(() => {
    const container = containerRef.current;
    if (!container) return;

    const chart = init(container, "dark", { renderer: "canvas" });
    chartRef.current = chart;
    const observer = new ResizeObserver(() => chart.resize());
    observer.observe(container);
    return () => {
      observer.disconnect();
      chart.dispose();
      chartRef.current = null;
    };
  }, []);

  useEffect(() => {
    const chart = chartRef.current;
    if (!chart) return;

    const currentOption = chart.getOption();
    const currentZoom = (currentOption?.dataZoom ?? []) as Array<{
      start?: number;
      end?: number;
    }>;
    chart.setOption(option, { notMerge: false, lazyUpdate: true });
    currentZoom.forEach((zoom, dataZoomIndex) => {
      if (zoom.start === undefined || zoom.end === undefined) return;
      chart.dispatchAction({
        type: "dataZoom",
        dataZoomIndex,
        start: zoom.start,
        end: zoom.end,
      });
    });
  }, [option]);

  return (
    <div
      ref={containerRef}
      className="interactive-chart"
      role="img"
      aria-label={ariaLabel}
    />
  );
}
