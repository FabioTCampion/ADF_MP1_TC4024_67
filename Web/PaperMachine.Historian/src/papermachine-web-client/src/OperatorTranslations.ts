const approvedVariableLabels: Record<string, string> = {
  headBoxMMH2O: "Pressão da caixa de entrada",
  headboxLipsPosition_mm: "Abertura do lábio da caixa de entrada",
  whiteWaterSiloCtrlOutput: "Saída do controle do silo de água branca",
  whiteWaterSiloLevel: "Nível do silo de água branca",
  formingBoardSuctionRollSpeed: "Velocidade do rolo de sucção da mesa formadora",
  formingBoardSuctionRollTorque: "Torque do rolo de sucção da mesa formadora",
  formingBoardTractionRollSpeed: "Velocidade do rolo de tração da mesa formadora",
  formingBoardTractionRollTorque: "Torque do rolo de tração da mesa formadora",
  firstPressSectionSpeed: "Velocidade da primeira prensa",
  firstPressSectionTorque: "Torque da primeira prensa",
  secondPressSectionSpeed: "Velocidade da segunda prensa",
  secondPressSectionTorque: "Torque da segunda prensa",
  stockPumpSpeed: "Velocidade da bomba de massa",
  stockPumpTorque: "Torque da bomba de massa",
  stockPumpState: "Estado da bomba de massa",
  stockTankLevel: "Nível do tanque de massa",
  stockPumpFlowM3h: "Vazão filtrada da massa",
  stockPumpVfdStartCmd: "Comando de partida do inversor da bomba de massa",
  stockPumpVfdResetCmd: "Comando de reset do inversor da bomba de massa",
  stockPumpSpeedReferencePct: "Referência de velocidade da bomba de massa",
  stockPumpDryMassSetpointKgH: "Referência de massa seca",
  stockPumpDryMassFeedbackKgH: "Massa seca calculada",
  stockPumpFlowTheoreticalM3h: "Vazão teórica da massa",
  stockPumpFlowCorrectedM3h: "Vazão corrigida da massa",
  stockPumpFlowLimitedM3h: "Vazão limitada da massa",
  stockPumpFlowSetpointM3h: "Referência de vazão da massa",
  stockPumpConsistencyFilteredPct: "Consistência filtrada da massa",
  stockPumpConsistencyUsedPct: "Consistência utilizada no controle",
  stockTankLevelSignalInvalid: "Sinal inválido do nível do tanque de massa",
  stockPumpPidErrorM3h: "Erro do PID de vazão da massa",
  stockPumpPidOutputPct: "Saída do PID da bomba de massa",
  stockPumpPidOutputSaturated: "Saída do PID da bomba de massa saturada",
  stockPumpSuggestedCalibrationFactor: "Fator de calibração sugerido",
  stockPumpSuggestedCalibrationFactorValid: "Fator de calibração sugerido válido",
  stockPumpFlowCalculationValid: "Cálculo de vazão da massa válido",
  stockPumpAutomaticControlValid: "Controle automático da bomba de massa válido",
  stockPumpAutomaticControlInvalid: "Controle automático da bomba de massa inválido",
  stockPumpConsistencySignalInvalid: "Sinal de consistência inválido",
  stockPumpFlowSignalInvalid: "Sinal de vazão da massa inválido",
  stockPumpBasisWeightSetpointInvalid: "Referência de gramatura inválida",
  stockPumpEffectiveWidthInvalid: "Largura efetiva inválida",
  stockPumpWireSpeedInvalid: "Velocidade da tela inválida",
  stockPumpCalibrationFactorInvalid: "Fator de calibração inválido",
  stockPumpOperatorTrimFactorInvalid: "Ajuste fino do operador inválido",
  stockPumpFlowSetpointLimited: "Referência de vazão limitada",
  stockPumpSpeedReferenceLimited: "Referência de velocidade limitada",
  stockPumpUsingLastValidConsistency: "Usando a última consistência válida",
  stockPumpFlowDeviationWarning: "Aviso de desvio de vazão da massa",
  stockPumpFlowDeviationAlarm: "Alarme de desvio de vazão da massa",
  stockPumpWarningActive: "Aviso ativo da bomba de massa",
  stockPumpAlarmActive: "Alarme ativo da bomba de massa",
  stockPumpRunning: "Bomba de massa em funcionamento",
  stockPumpControlActive: "Controle da bomba de massa ativo",
  stockPumpManualActive: "Controle manual da bomba de massa ativo",
  stockPumpAutomaticActive: "Controle automático da bomba de massa ativo",
  stockPumpDeviceState: "Estado do inversor da bomba de massa",
  stockPumpCurrent: "Corrente da bomba de massa",
  effectivePaperPresence: "Papel confirmado (sensor G3 + bomba de massa)",
  mixPumpSpeed: "Velocidade da bomba de mistura",
  mixPumpTorque: "Torque da bomba de mistura",
  winderSpeedMPM: "Velocidade da enroladeira",
  winderTorque: "Torque da enroladeira",
  winderPaperPresence: "Papel presente na enroladeira",
};

// Substituições revisadas pelo usuário na planilha de traduções do analisador.
const reviewedVariableLabels: Record<string, string> = {
  couchPitPumpSpeed: "Velocidade da bomba do Couch Pit",
  wirePitPumpSpeed: "Velocidade da bomba do Wire Pit",
  couchPitMixerState: "Estado do agitador do Couch Pit",
  couchPitPumpFaultCode: "Código de falha — Bomba do Couch Pit",
  couchPitPumpFaultEventCounter: "Contador de falhas — Bomba do Couch Pit",
  couchPitPumpState: "Estado — Bomba do Couch Pit",
  dryingSectionLubricationPump1State:
    "Estado da bomba 1 do sistema de lubrificação da seção de secagem",
  dryingSectionLubricationPump2State:
    "Estado da bomba 2 do sistema de lubrificação da seção de secagem",
  firstPressSectionState: "Estado da primeira prensa",
  formingBoardSuctionRollFaultCode:
    "Código de falha — Rolo de sucção da mesa formadora",
  formingBoardSuctionRollFaultEventCounter:
    "Contador de falhas — Rolo de sucção da mesa formadora",
  formingBoardSuctionRollState: "Estado — Rolo de sucção da mesa formadora",
  formingBoardTractionRollFaultCode:
    "Código de falha — Rolo de tração da mesa formadora",
  formingBoardTractionRollFaultEventCounter:
    "Contador de falhas — Rolo de tração da mesa formadora",
  formingBoardTractionRollState: "Estado — Rolo de tração da mesa formadora",
  formingTailCutterNozzleState: "Estado do bico de corte",
  formingWireShowerState: "Estado do chuveiro da tela formadora",
  lowVacuumExhaustFanState: "Estado do exaustor do sistema de baixo vácuo",
  mixingPumpFaultCode: "Código de falha — Bomba de mistura",
  mixingPumpFaultEventCounter: "Contador de falhas — Bomba de mistura",
  pressFeltVaccum1State: "Estado do sistema de vácuo 1 do feltro da prensa",
  pressFeltVaccum2State: "Estado do sistema de vácuo 2 do feltro da prensa",
  pressSectionFirstPressFaultCode: "Código de falha — Primeira prensa",
  pressSectionFirstPressFaultEventCounter: "Contador de falhas — Primeira prensa",
  pressSectionSecondPressFaultCode: "Código de falha — Segunda prensa",
  pressSectionSecondPressFaultEventCounter: "Contador de falhas — Segunda prensa",
  secondPressSectionState: "Estado da segunda prensa",
  suctionRollVacuumPumpState: "Estado da bomba de vácuo do rolo de sucção",
  vacuumSealPumpState: "Estado da bomba de água de selagem do sistema de vácuo",
  vacuumWaterSepPumpState:
    "Estado da bomba do separador de água do sistema de vácuo",
  wirePitPumpFaultCode: "Código de falha — Bomba do Wire Pit",
  wirePitPumpFaultEventCounter: "Contador de falhas — Bomba do Wire Pit",
  wirePitPumpState: "Estado — Bomba do Wire Pit",
  wireVacuumPumpState: "Estado da bomba de vácuo da tela formadora",
  headBoxMMH2OCalc: "Pressão calculada da caixa de entrada",
  pressureScreenState: "Estado do depurador pressurizado",
  winderPressureSetpointDriveSide:
    "Pressão de referência da enroladeira — Lado do acionamento",
  winderPressureSetpointOperatorSide:
    "Pressão de referência da enroladeira — Lado do operador",
  couchPitLevel: "Nível do Couch Pit",
  mixPumpRatio: "Relação de velocidade da bomba de mistura",
  mixPumpSetpointMan: "Referência manual da bomba de mistura",
  wirePitLevel: "Nível do Wire Pit",
  dryingSectionLubricationCircuitTemperature:
    "Temperatura do circuito de lubrificação da seção de secagem",
  dryingSectionLubricationTankTemperature:
    "Temperatura do tanque de lubrificação da seção de secagem",
  couchPitPumpFaultTorque: "Torque no momento da falha — Bomba do Couch Pit",
  couchPitPumpTorque: "Torque da bomba do Couch Pit",
  formingBoardSuctionRollFaultTorque:
    "Torque no momento da falha — Rolo de sucção da mesa formadora",
  formingBoardTractionRollFaultTorque:
    "Torque no momento da falha — Rolo de tração da mesa formadora",
  mixingPumpFaultTorque: "Torque no momento da falha — Bomba de mistura",
  pressSectionFirstPressFaultTorque:
    "Torque no momento da falha — Primeira prensa",
  pressSectionSecondPressFaultTorque:
    "Torque no momento da falha — Segunda prensa",
  wirePitPumpFaultTorque: "Torque no momento da falha — Bomba do Wire Pit",
  wirePitPumpTorque: "Torque da bomba do Wire Pit",
  headBoxJetSpeedMPS: "Velocidade do jato da caixa de entrada",
  presseGroupSpeedDif: "Diferença de velocidade da seção de prensas",
  winderSpeedDif: "Diferença de velocidade da enroladeira",
};

const exactVariableLabels: Record<string, string> = {
  ...approvedVariableLabels,
  ...reviewedVariableLabels,
};

const locationLabels: Record<string, string> = {
  Upper: "Superior",
  Lower: "Inferior",
};

const equipmentLabels: Record<string, string> = {
  couchPitPump: "Bomba do Couch Pit",
  wirePitPump: "Bomba do Wire Pit",
  stockPump: "Bomba de massa",
  mixPump: "Bomba de mistura",
  winder: "Enroladeira",
};

const measurementLabels: Record<string, string> = {
  SpeedMPM: "Velocidade",
  Speed: "Velocidade",
  Torque: "Torque",
  FaultCode: "Código de falha",
  FaultEventCounter: "Contador de falhas",
  FaultTorque: "Torque no momento da falha",
  State: "Estado",
};

const categoryLabels: Record<string, string> = {
  Headbox: "Caixa de entrada",
  Processo: "Processo",
  Torque: "Torque",
  Bombas: "Bombas",
  Velocidade: "Velocidade",
  Pressão: "Pressão",
  Posição: "Posição",
  Temperatura: "Temperatura",
  Estado: "Estado",
};

const evidenceKindLabels: Record<string, string> = {
  Alarm: "Alarme",
  Alarme: "Alarme",
  Command: "Comando",
  Comando: "Comando",
  Status: "Estado",
};

const exactCommandLabels: Record<string, string> = {
  machineReset: "Reinicializar a máquina",
  formingBoardRollUpCmd: "Subir a mesa formadora",
  formingBoardRollDownCmd: "Descer a mesa formadora",
  headboxLipsTargetSetpoint_mm: "Abertura desejada do lábio da caixa de entrada",
  headboxLipsAutoAdjustCmd: "Iniciar ajuste automático do lábio da caixa de entrada",
  headboxLipsManualOpenCmd: "Abrir manualmente o lábio da caixa de entrada",
  headboxLipsManualCloseCmd: "Fechar manualmente o lábio da caixa de entrada",
  formingTailCutterNozzleFwCmd: "Avançar o bico de corte",
  formingTailCutterNozzleRevCmd: "Recuar o bico de corte",
  Group1RecoverIncrement: "Incremento de recuperação — Secagem · Grupo 1",
  dryingSectionLubricationPumpSelector:
    "Selecionar bomba — Lubrificação da secagem",
  winderPressureOperatorSideIncCmd:
    "Aumentar pressão — Enroladeira · Lado do operador",
  winderPressureOperatorSideDecCmd:
    "Diminuir pressão — Enroladeira · Lado do operador",
  winderPressureDriveSideIncCmd:
    "Aumentar pressão — Enroladeira · Lado do acionamento",
  winderPressureDriveSideDecCmd:
    "Diminuir pressão — Enroladeira · Lado do acionamento",
  winderRiderRollWorkPositionCmd:
    "Mover rolo cavaleiro para posição de trabalho — Enroladeira",
  winderRiderRollReleasePositionCmd:
    "Mover rolo cavaleiro para posição liberada — Enroladeira",
  winderEnableTorqueControlCmd: "Habilitar controle de torque — Enroladeira",
  winderTorqueSetpoint: "Referência de torque — Enroladeira",
  winderCoreLockAutoMan: "Modo automático/manual — Travamento do mandril",
  winderCoreLockCmd: "Travar mandril — Enroladeira",
  winderCoreUnlockCmd: "Destravar mandril — Enroladeira",
  winderCoreAceleratorEngageCmd: "Acoplar acelerador do mandril — Enroladeira",
  winderCoreAceleratorDisengageCmd:
    "Desacoplar acelerador do mandril — Enroladeira",
  winderCoreBrakeAutoMan: "Modo automático/manual — Freio do mandril",
  winderCoreBrakeEnableCmd: "Habilitar freio do mandril — Enroladeira",
  winderCoreBrakeDisableCmd: "Desabilitar freio do mandril — Enroladeira",
  winderCorePositioningArmFwdCmd:
    "Avançar braço posicionador do mandril — Enroladeira",
  winderCorePositioningArmRevCmd:
    "Recuar braço posicionador do mandril — Enroladeira",
  winderCorePositioningArmPosLimitFwd:
    "Fim de curso avançado do braço posicionador — Enroladeira",
  winderCorePositioningArmPosLimitRev:
    "Fim de curso recuado do braço posicionador — Enroladeira",
};

const commandEquipmentLabels: Array<[string, string]> = [
  ["dryingSectionLubricationPump", "Lubrificação da secagem"],
  ["dryingSectionGroup1", "Secagem · Grupo 1"],
  ["dryingSectionGroup2", "Secagem · Grupo 2"],
  ["dryingSectionGroup3", "Secagem · Grupo 3"],
  ["formingTailCutterNozzle", "Bico de corte"],
  ["formingWireShower", "Chuveiro da tela formadora"],
  ["formingBoard", "Mesa formadora"],
  ["pressVacuumPump1", "Bomba de vácuo 1 da prensa"],
  ["pressVacuumPump2", "Bomba de vácuo 2 da prensa"],
  ["pressSection", "Seção de prensas"],
  ["vacuumWaterSepPump", "Bomba do separador de água do vácuo"],
  ["suctionRollVacuumPump", "Bomba de vácuo do rolo de sucção"],
  ["vacuumSealPump", "Bomba de água de selagem do vácuo"],
  ["wireVacuumPump", "Bomba de vácuo da tela formadora"],
  ["lowVacuumExhaustFan", "Exaustor do sistema de baixo vácuo"],
  ["pressureScreen", "Depurador pressurizado"],
  ["couchPitMixer", "Agitador do Couch Pit"],
  ["couchPitPump", "Bomba do Couch Pit"],
  ["wirePitPump", "Bomba do Wire Pit"],
  ["wirePit", "Wire Pit"],
  ["whiteWaterSilo", "Silo de água branca"],
  ["stockPump", "Bomba de massa"],
  ["mixPump", "Bomba de mistura"],
  ["winderSection", "Enroladeira"],
  ["wireSection", "Tela formadora"],
];

const commandActionLabels: Array<[string, string]> = [
  ["PaperBreakSteamPressure1", "Pressão de vapor 1 para quebra de papel"],
  ["PaperBreakSteamPressure2", "Pressão de vapor 2 para quebra de papel"],
  ["SteamPressureManualSetpoint", "Referência manual da pressão de vapor"],
  ["SteamPressureAutoSetpoint", "Referência automática da pressão de vapor"],
  ["SteamPressureAutoMan", "Modo automático/manual da pressão de vapor"],
  ["RecoverIncrement", "Incremento de recuperação"],
  ["RecoverSpeedCmd", "Recuperar velocidade"],
  ["ProductionSpeed", "Velocidade de produção"],
  ["ManualSetpoint", "Referência manual de velocidade"],
  ["SetpointManInc", "Aumentar referência manual"],
  ["SetpointManDec", "Diminuir referência manual"],
  ["SetpointAutoInc", "Aumentar referência automática"],
  ["SetpointAutoDec", "Diminuir referência automática"],
  ["SetpointMan", "Referência manual"],
  ["SetpointAuto", "Referência automática"],
  ["SpeedInc", "Aumentar velocidade"],
  ["SpeedDec", "Diminuir velocidade"],
  ["FastStop", "Parada rápida"],
  ["AutoMan", "Modo automático/manual"],
  ["RatioInc", "Aumentar proporção de mistura"],
  ["RatioDec", "Diminuir proporção de mistura"],
  ["Ratio", "Proporção de mistura"],
  ["StretchExtendCmd", "Esticar"],
  ["StretchReleaseCmd", "Aliviar tensão"],
  ["TensionLimitMax", "Limite máximo de tensão"],
  ["TensionLimitMin", "Limite mínimo de tensão"],
  ["DisplacementOpSide", "Deslocamento — Lado do operador"],
  ["DisplacementDriveSide", "Deslocamento — Lado do acionamento"],
  ["StartCmd", "Ligar"],
  ["StopCmd", "Parar"],
  ["Start", "Ligar"],
  ["Stop", "Parar"],
];

export function operatorVariableLabel(field: string) {
  if (exactVariableLabels[field]) return exactVariableLabels[field];

  const dryingMotor = field.match(
    /^dryingSectionGroup(\d)(Upper|Lower)(Master|Slave(\d+))(SpeedMPM|Speed|Torque|FaultCode|FaultEventCounter|FaultTorque|State)$/,
  );
  if (dryingMotor) {
    const [, group, location, role, slaveNumber, measurement] = dryingMotor;
    const roleLabel = role === "Master" ? "Mestre" : `Escravo ${slaveNumber}`;
    const measurementLabel = measurementLabels[measurement] ?? measurement;
    return `${measurementLabel} da secagem G${group} · ${locationLabels[location]} · ${roleLabel}`;
  }

  const steamPressure = field.match(/^dryingSectionGroup(\d)SteamPressure$/);
  if (steamPressure) return `Pressão de vapor da secagem G${steamPressure[1]}`;

  const paperPresence = field.match(/^dryingSectionGroup(\d)PaperPresence$/);
  if (paperPresence) return `Papel presente na secagem G${paperPresence[1]}`;

  const speedDifference = field.match(/^dryingSectionGroup(\d)SpeedDif$/);
  if (speedDifference)
    return `Diferença de velocidade da secagem G${speedDifference[1]}`;

  for (const [prefix, equipment] of Object.entries(equipmentLabels)) {
    if (!field.startsWith(prefix)) continue;
    const measurement = field.slice(prefix.length);
    if (measurementLabels[measurement])
      return `${measurementLabels[measurement]} — ${equipment}`;
  }

  return field
    .replace(/whiteWaterSilo/gi, "Silo de água branca ")
    .replace(/wirePit/gi, "Wire Pit ")
    .replace(/couchPit/gi, "Couch Pit ")
    .replace(/dryingSection/gi, "Secagem ")
    .replace(/formingBoard/gi, "Mesa formadora ")
    .replace(/headBox/gi, "Caixa de entrada ")
    .replace(/FaultEventCounter/gi, " Contador de falhas")
    .replace(/FaultTorque/gi, " Torque no momento da falha")
    .replace(/FaultCode/gi, " Código de falha")
    .replace(/PaperPresence/gi, " Papel presente")
    .replace(/SteamPressure/gi, " Pressão de vapor")
    .replace(/SpeedMPM/gi, " Velocidade")
    .replace(/Speed/gi, " Velocidade")
    .replace(/Torque/gi, " Torque")
    .replace(/Position/gi, " Posição")
    .replace(/Temperature/gi, " Temperatura")
    .replace(/Upper/gi, " Superior")
    .replace(/Lower/gi, " Inferior")
    .replace(/Master/gi, " Mestre")
    .replace(/Slave(\d+)/gi, " Escravo $1")
    .replace(/Group(\d+)/gi, " G$1")
    .replace(/Pump/gi, " Bomba")
    .replace(/Pressure/gi, " Pressão")
    .replace(/Level/gi, " Nível")
    .replace(/Setpoint/gi, " Referência")
    .replace(/CtrlOutput/gi, " Saída do controle")
    .replace(/Ratio/gi, " Proporção")
    .replace(/Current/gi, " Corrente")
    .replace(/Voltage/gi, " Tensão")
    .replace(/Frequency/gi, " Frequência")
    .replace(/Diameter/gi, " Diâmetro")
    .replace(/State/gi, " Estado")
    .replace(/EventCounter/gi, " Contador de eventos")
    .replace(/([a-z0-9])([A-Z])/g, "$1 $2")
    .replace(/_/g, " ")
    .replace(/\s+/g, " ")
    .trim();
}

export function operatorCommandLabel(command: string) {
  if (exactCommandLabels[command]) return exactCommandLabels[command];

  for (const [prefix, equipment] of commandEquipmentLabels) {
    if (!command.startsWith(prefix)) continue;
    const actionName = command.slice(prefix.length);
    const action = commandActionLabels.find(([suffix]) => suffix === actionName)?.[1];
    if (action) return `${action} — ${equipment}`;
  }

  return operatorVariableLabel(command)
    .replace(/\bFast Stop\b/gi, "Parada rápida")
    .replace(/\bAuto Man\b/gi, "Modo automático/manual")
    .replace(/\bStart\b/gi, "Ligar")
    .replace(/\bStop\b/gi, "Parar")
    .replace(/\bInc\b/gi, "Aumentar")
    .replace(/\bDec\b/gi, "Diminuir")
    .replace(/\bCmd\b/gi, "Comando")
    .replace(/\bManual\b/gi, "Manual")
    .replace(/\s+/g, " ")
    .trim();
}

export function operatorCategoryLabel(category: string) {
  return categoryLabels[category] ?? category;
}

export function operatorEvidenceKindLabel(kind: string) {
  return evidenceKindLabels[kind] ?? kind;
}
