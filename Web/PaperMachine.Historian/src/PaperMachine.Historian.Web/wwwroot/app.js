const dateFormatter = new Intl.DateTimeFormat("pt-BR", {
  dateStyle: "short",
  timeStyle: "medium"
});

const formatDate = value => value ? dateFormatter.format(new Date(value)) : "—";
const formatValue = value => {
  if (value === null || value === undefined) return "—";
  if (typeof value === "boolean") return value ? "Ativo" : "Inativo";
  return String(value);
};

function cell(value) {
  const element = document.createElement("td");
  element.textContent = value;
  return element;
}

async function getJson(url) {
  const response = await fetch(url, { headers: { Accept: "application/json" } });
  if (!response.ok) throw new Error(`${response.status} ${response.statusText}`);
  return response.json();
}

async function refreshRuntime() {
  const runtime = await getJson("/api/runtime");
  const indicator = document.querySelector("#connection");
  indicator.textContent = runtime.adsConnected ? "ADS conectado" : "ADS desconectado";
  indicator.className = runtime.adsConnected
    ? "connection connection--online"
    : "connection connection--offline";
  indicator.title = runtime.lastError || "";
  document.querySelector("#last-read").textContent = formatDate(runtime.lastSuccessfulReadAtUtc);
  document.querySelector("#mapping-version").textContent = runtime.mappingVersion || "—";
}

async function refreshCurrent() {
  const current = await getJson("/api/current");
  const grid = document.querySelector("#status-grid");
  grid.replaceChildren();
  Object.entries(current.status).forEach(([name, value]) => {
    const item = document.createElement("article");
    const label = document.createElement("span");
    const strong = document.createElement("strong");
    label.textContent = name;
    strong.textContent = formatValue(value);
    item.append(label, strong);
    grid.append(item);
  });
}

async function refreshAlarms() {
  const alarms = await getJson("/api/history/alarms?limit=100");
  const body = document.querySelector("#alarms");
  body.replaceChildren();
  alarms.forEach(alarm => {
    const row = document.createElement("tr");
    if (!alarm.clearedAtUtc) row.classList.add("row--active");
    const duration = alarm.durationMilliseconds === null
      ? "Em andamento"
      : `${Math.round(alarm.durationMilliseconds / 1000)} s`;
    row.append(
      cell(alarm.alarmName),
      cell(formatDate(alarm.activatedAtUtc)),
      cell(formatDate(alarm.clearedAtUtc)),
      cell(duration)
    );
    body.append(row);
  });
  document.querySelector("#active-alarm-count").textContent =
    alarms.filter(alarm => !alarm.clearedAtUtc).length;
}

async function refreshCommands() {
  const commands = await getJson("/api/history/commands?limit=100");
  const body = document.querySelector("#commands");
  body.replaceChildren();
  commands.forEach(command => {
    const row = document.createElement("tr");
    row.append(
      cell(command.commandName),
      cell(command.previousValueJson ?? "—"),
      cell(command.currentValueJson),
      cell(formatDate(command.observedAtUtc))
    );
    body.append(row);
  });
}

async function refresh() {
  const jobs = [refreshRuntime(), refreshAlarms(), refreshCommands(), refreshCurrent()];
  const results = await Promise.allSettled(jobs);
  const failure = results.find(result => result.status === "rejected");
  if (failure) console.warn("Atualização parcial do painel:", failure.reason);
}

document.querySelectorAll("[data-refresh]").forEach(button =>
  button.addEventListener("click", refresh));

refresh();
setInterval(refresh, 5000);
