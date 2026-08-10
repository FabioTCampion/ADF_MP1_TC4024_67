## CPNTeck Paper Machine Historian 0.3.7

### Consulta gerencial de pesos

- adiciona a página **Pesos** ao menu principal, com consulta autenticada das
  pesagens capturadas pelo PLC;
- oferece períodos rápidos para hoje, últimas 24 horas, ontem, 7 dias e 30 dias,
  além de intervalo personalizado de até 366 dias;
- permite buscar por jumbo, ordem de produção, produto ou contador de evento;
- consolida quantidade de capturas, peso acumulado, peso médio, mínimo e máximo;
- apresenta a produção diária em toneladas, quantidade de jumbos, média, faixa
  de peso e número de ajustes;
- consolida a produção por produto e gramatura com participação percentual no
  peso do período;
- mantém o histórico detalhado com peso capturado e peso efetivamente
  considerado.

### Correção auditada por supervisores

- adiciona o perfil de acesso **Supervisor**, configurável por administradores;
- restringe a correção de pesos a supervisores e administradores;
- exige um motivo para cada alteração e registra responsável e horário;
- preserva o peso original recebido do PLC e mantém o histórico completo das
  correções na tabela `JumboWeightCorrections`;
- migra automaticamente o SQLite para o schema 12;
- disponibiliza a operação autenticada em
  `PUT /api/production/weights/{id}`.

Esta Release atualiza somente o Historian. Ela não realiza download nem altera
o projeto TwinCAT em execução.
