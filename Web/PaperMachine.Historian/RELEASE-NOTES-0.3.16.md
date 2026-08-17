## Atualização 0.3.16

- Passa a usar `paperMachineHmiCommands.mixPumpRatio` como fonte oficial do
  ratio da bomba de mistura.
- Registra o baseline do ratio na inicialização, mantém a auditoria das
  alterações e amostra o valor junto da telemetria.
- Desconsidera ratios fora da faixa operacional configurada de `0,5` a `2,0`.
- Atualiza **Melhores condições** para as 11 famílias de parâmetros aprovadas,
  totalizando 17 séries operacionais.
- Inclui fluxo de massa, pressão da caixa de entrada, abertura do lábio,
  velocidade da bomba de massa e torques dos rolos de sucção e tração.
- Mantém pressões, passes e torques dos três grupos de secagem.
- Calcula o torque médio de cada grupo usando somente acionamentos ativos.
- Remove temporariamente os parâmetros da enroladeira do analisador.
- Inclui backlog e critérios objetivos para a validação operacional do
  analisador antes dos próximos ajustes estatísticos.

O pacote é autocontido para Windows x64 e mantém a atualização automática do
banco de dados na inicialização do serviço.
