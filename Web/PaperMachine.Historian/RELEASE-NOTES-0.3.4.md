## CPNTeck Paper Machine Historian 0.3.4

### Correção do HTTP 503 na instalação

- remove as consultas automáticas de status e log da contagem do limitador de
  requisições;
- mantém o limite de segurança somente nas ações administrativas `POST` de
  verificar, baixar e instalar;
- evita que a própria atualização da tela a cada poucos segundos bloqueie o
  botão **Instalar**;
- apresenta uma mensagem didática caso o limite real de ações seja atingido.

### Conteúdo acumulado da 0.3.3

- correção do BOM UTF-8 no conector do Windows 10 LTSC;
- validação do conector como serviço Windows real;
- log da última instalação disponível na página **Atualizações**, com limites
  de leitura e ocultação de credenciais.

O servidor na versão 0.2.3 pode instalar diretamente a versão 0.3.4. Não é
necessário instalar primeiro a 0.3.3 e não use `-Force`.
