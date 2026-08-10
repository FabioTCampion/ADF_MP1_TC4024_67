## CPNTeck Paper Machine Historian 0.3.8

### Novas variáveis da bomba de massa

- atualiza o contrato ADS para `paper-machine-hmi-v5` e incorpora as 42 novas
  variáveis escalares publicadas em `paperMachineHmiStatus` pelo TwinCAT;
- registra nível do tanque, vazões medida, teórica, corrigida, limitada e de
  referência;
- registra referência e retorno de massa seca, consistência filtrada e
  consistência efetivamente utilizada pelo controle;
- registra erro e saída do PID, referência de velocidade, corrente e fator de
  calibração sugerido;
- registra validade dos sinais, saturação, limitações, desvios, avisos, alarmes
  e os modos de operação manual e automático;
- apresenta nomes revisados em português no status atual, históricos e seleção
  de variáveis dos gráficos;
- aplica unidades de engenharia específicas: `m³/h`, `kg/h`, `%` e `A`.

### Persistência e atualização

- migra automaticamente o SQLite para o schema 13;
- adiciona as novas medições e estados às tabelas otimizadas de telemetria e
  aos consolidados por minuto, inclusive em bancos criados por versões
  anteriores;
- mantém os novos campos disponíveis nos snapshots detalhados e diagnósticos de
  quebra;
- atualiza automaticamente `Historian:MappingVersion` durante a instalação,
  mesmo quando o arquivo de configuração do servidor é preservado de uma
  versão anterior.

A estrutura de controladores de consistência que permanece comentada no projeto
TwinCAT não faz parte deste mapeamento. Esta Release atualiza somente o
Historian e não realiza download nem alteração do PLC.
