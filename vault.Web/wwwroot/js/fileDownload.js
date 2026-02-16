window.vaultWeb = window.vaultWeb || {};

window.vaultWeb.downloadFileFromStream = async (fileName, contentStreamReference) => {
  const arrayBuffer = await contentStreamReference.arrayBuffer();
  const blob = new Blob([arrayBuffer]);
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement("a");
  anchor.href = url;
  anchor.download = fileName || "download.bin";
  anchor.style.display = "none";
  document.body.appendChild(anchor);
  anchor.click();
  anchor.remove();
  URL.revokeObjectURL(url);
};

window.vaultWeb.prompt = (message, defaultValue) => {
  const result = window.prompt(message, defaultValue ?? "");
  return result;
};

window.vaultWeb.confirm = (message) => {
  return window.confirm(message);
};
