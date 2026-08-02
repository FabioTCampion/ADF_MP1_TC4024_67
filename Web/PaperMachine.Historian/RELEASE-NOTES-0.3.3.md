## CPNTeck Paper Machine Historian 0.3.3

### Correção definitiva do conector no Windows 10 LTSC

- aceita configurações JSON com BOM UTF-8 criadas pelo Windows PowerShell 5.1;
- passa a gravar a configuração do conector em UTF-8 sem BOM;
- valida a configuração antes de criar ou iniciar o serviço;
- conecta o processo ao Windows Service Control Manager antes de carregar a
  configuração, evitando timeout sem diagnóstico;
- inclui teste administrativo isolado que executa o conector como serviço
  Windows real e valida o endpoint `/health`.

### Diagnóstico pela página de atualizações

- mostra o log da última instalação diretamente na área administrativa;
- atualiza o conteúdo automaticamente e permite copiar as últimas linhas;
- limita a leitura a 256 KB e não aceita caminhos fornecidos pelo navegador;
- oculta possíveis tokens, chaves de API, senhas e cabeçalhos de autorização;
- mantém o log disponível depois de uma atualização bem-sucedida ou rollback.

### Validação

- conector iniciado como serviço Windows real e `/health` validado;
- 41 testes .NET aprovados;
- testes Go, lint e build do frontend aprovados.

O servidor na versão 0.2.3 deve instalar diretamente a versão 0.3.3. Não
reinstale as versões 0.3.0, 0.3.1 ou 0.3.2 e não use `-Force`.
