import { readFileSync } from "node:fs";

export const HTML = readFileSync(new URL("../../assets/index.html", import.meta.url), "utf8");
export const STYLES = readFileSync(new URL("../../assets/styles.css", import.meta.url), "utf8");
export const APP_JS = readFileSync(new URL("../../assets/app.js", import.meta.url), "utf8");
