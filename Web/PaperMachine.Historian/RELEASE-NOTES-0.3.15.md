# PaperMachine Historian 0.3.15

## Melhores condições de produção

- adiciona a página **Melhores condições** para comparar a produção atual com
  os melhores trechos históricos do mesmo produto e gramatura;
- permite priorizar equilíbrio, estabilidade ou velocidade no ranking das
  campanhas e consultar janelas de 30, 90 e 180 dias;
- calcula tempo contínuo sem quebra, velocidade média e máxima, oscilação,
  cobertura dos dados e faixas reais dos parâmetros de processo;
- usa `ProductionQualityPeriods`, agregados de telemetria e
  `PaperBreakEvents`, sem considerar largura na chave de comparação;
- expõe a API autenticada
  `GET /api/production/best-conditions` com validação de período, produto e
  gramatura;
- amplia a telemetria otimizada com estiramentos, caixa de entrada, relação
  jato/tela, nível de água branca e pressões da enroladeira;
- mantém fallback para snapshots legados nos gráficos enquanto as novas séries
  otimizadas acumulam histórico;
- trata ausência de integração ERP, contexto atual ou parâmetros históricos
  sem apresentar valores simulados como produção real.
