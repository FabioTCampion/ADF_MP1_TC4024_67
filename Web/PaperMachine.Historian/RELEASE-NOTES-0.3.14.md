# PaperMachine Historian 0.3.14

## Envio seguro de pesagens ao PaperSystem

- adiciona outbox SQLite transacional para pesagens, sem abrir portas externas
  no Historian;
- envia eventos `captured`, `corrected` e `voided` com `eventId` estável e
  revisão crescente;
- amplia o conector Go com o endpoint local
  `POST /v1/production/weights` e encaminhamento TLS 1.3 para
  `https://api.papersystem.com.br/apontamentos/cpnteck/jupia/mp/pesagens`;
- reutiliza a mesma `x-api-key` já instalada para a leitura do mapa de
  produção, mantendo-a isolada do processo do Historian;
- valida host, JSON, tamanho, idempotência e confirmação retornada pelo
  PaperSystem;
- aplica reenvio progressivo para falhas temporárias e suspende rejeições
  permanentes para inspeção;
- expõe o status autenticado da fila em
  `GET /api/production/weights/export-status`;
- atualiza instalador, configuração e script de chave para ativar o envio em
  instalações onde a integração PaperSystem já estiver habilitada.
