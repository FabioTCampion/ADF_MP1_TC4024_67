# PaperMachine Historian 0.3.10

## Correção crítica do update 0.3.9

- Corrige a inicialização do Historian após a inclusão da exclusão auditada de pesos.
- Declara explicitamente o corpo JSON da rota de exclusão, permitindo que o ASP.NET monte todos os endpoints e abra a porta HTTP normalmente.
- Adiciona teste de regressão que materializa as rotas da aplicação e valida o endpoint `DELETE /api/production/weights/{id}`.

## Rollback mais seguro

- A release que falhou é removida antes de os serviços anteriores serem reiniciados, evitando bloqueio do executável do conector.
- Uma falha secundária de limpeza não substitui mais a mensagem do erro original do update.

Esta versão substitui a v0.3.9. O equipamento que apresentou a falha permaneceu operacional na v0.3.8 após o rollback automático.
