## CPNTeck Paper Machine Historian 0.3.6

### Captura de peso dos jumbos

- migra automaticamente o SQLite para o schema 11 e cria a tabela dedicada
  `JumboWeightCaptures`;
- executa um worker independente dentro do backend, reutilizando o snapshot ADS
  validado e sem abrir uma segunda conexão ou escrever no PLC;
- identifica cada pesagem pela dupla contador de evento e FILETIME, evitando
  registros duplicados depois de reinícios do serviço;
- mantém o peso mesmo quando o ERP está indisponível;
- associa OP, produto, gramatura e largura quando o contexto ERP está ativo,
  recente e compatível com o horário real da captura;
- disponibiliza o histórico autenticado em `GET /api/production/weights` e a
  última pesagem em `GET /api/production/weights/latest`;
- atualiza o mapeamento ADS para `paper-machine-hmi-v4`.

### Tara das estangas no PLC

O projeto-fonte TwinCAT passa a suportar 20 pesos persistentes de estangas. O
CLP valida a seleção e grava em `jumboCapturedWeightKg` o peso líquido, depois de
descontar a tara escolhida. O Historian continua somente leitura e armazena esse
valor já calculado pelo PLC.

Esta Release instala somente o Historian. A nova lógica de tara exige compilação
e ativação separadas do projeto TwinCAT; o atualizador Web não realiza download
ou alteração no PLC.
