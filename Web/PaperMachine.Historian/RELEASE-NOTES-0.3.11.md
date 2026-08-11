# PaperMachine Historian 0.3.11

## Consistência da massa refinada

- Adiciona `refinedStockTankConsistencyFilteredPct` à telemetria periódica e às tendências de processo.
- Adiciona `refinedStockTankConsistencySignalInvalid` à telemetria periódica e às tendências de processo.
- Inclui nomes amigáveis e unidades para apresentação das duas variáveis na interface.

## Banco de dados e compatibilidade

- Atualiza o schema SQLite para a versão 15.
- Cria automaticamente as novas colunas em `TelemetrySamples` e `TelemetryMinuteAggregates`, inclusive em bancos existentes.
- Atualiza o mapeamento da estrutura do CLP para `paper-machine-hmi-v6`.
- Mantém os snapshots e os dados históricos existentes sem alteração.
