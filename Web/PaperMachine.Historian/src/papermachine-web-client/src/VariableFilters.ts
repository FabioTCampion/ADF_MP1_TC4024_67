export type SharedVariableFilter = {
  id: number;
  userId: number;
  name: string;
  variables: string[];
  isDefault: boolean;
  revision: number;
  createdAtUtc: string;
  updatedAtUtc: string;
};

export function sortVariableFilters(filters: SharedVariableFilter[]) {
  return [...filters].sort((left, right) =>
    Number(right.isDefault) - Number(left.isDefault) ||
    left.name.localeCompare(right.name, "pt-BR"));
}
