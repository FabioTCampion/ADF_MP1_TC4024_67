## Correção

- Corrige a interpretação do código EtherCAT `603Fh` dos inversores Delta C2000 Plus.
- Substitui a tabela sequencial de falhas do teclado pela tabela de códigos de 16 bits do perfil CiA 402.
- O valor decimal `12832` agora é apresentado como `0x3220 — Subtensão no barramento CC`.
- Mantém o código bruto decimal e hexadecimal no histórico para rastreabilidade.
- Códigos não cadastrados deixam de receber uma descrição Delta incorreta e são identificados como códigos CiA 402 ainda não cadastrados.

## Validação

- Caso real `12832 / 0x3220` validado no processamento e na persistência SQLite.
- Modelo mantido como `Delta C2000 Plus` para todos os inversores.
- 35 testes automatizados aprovados.
