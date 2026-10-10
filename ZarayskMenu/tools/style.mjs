import fs from 'node:fs';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const c = JSON.parse(fs.readFileSync(path.join(root,'config/layout.json'),'utf8'));
for (const k of ['Left','Top','Width','RadarRight','ExtraRight','RadarBottom','ChatTop','RowHeight','FontSize'])
  if (!Number.isFinite(c[k]) || c[k] < 0 || c[k] > 2000) throw Error(`Invalid ${k}`);
for (const k of ['Background','Accent','NumberColor','ItemColor']) if (!/^#[a-fA-F0-9]{6}([a-fA-F0-9]{2})?$/.test(c[k])) throw Error(`Invalid ${k}`);
// Full seven-item page and three compact control rows, including padding/borders.
const height = 2 + 24 + 31 + 29 + c.RowHeight * 7 + 7 + 1 + 7 + 30 * 3 + 2;
if(c.Width < 220 || c.RowHeight < 22 || c.FontSize < 12 || c.FontSize > c.RowHeight-4 || c.Left+c.Width > c.RadarRight+c.ExtraRight || c.Top < c.RadarBottom || c.Top+height > c.ChatTop)
  throw Error(`Menu outside configured radar/chat bounds (bottom=${c.Top+height}). Adjust layout.json.`);
const file=path.join(root,'panorama/styles/custom_game/zaraysk_menu.css');
let css=fs.readFileSync(file,'utf8').split('/* GENERATED GEOMETRY */')[0];
css += `/* GENERATED GEOMETRY */\n.window { width: ${c.Width}px; background-color: ${c.Background}; }\n.window.shown { position: ${c.Left}px ${c.Top}px 0px; }\n.accent { background-color: ${c.Accent}; }\n.row { height: ${c.RowHeight}px; }\n.number { font-size: ${c.FontSize}px; color: ${c.NumberColor}; }\n.item { font-size: ${c.FontSize}px; color: ${c.ItemColor}; }\n.compact.window { width: ${Math.min(276,c.Width)}px; }\n.compact .row { height: ${Math.min(25,c.RowHeight)}px; }\n.compact .number { font-size: ${Math.min(16,c.FontSize)}px; }\n.compact .item { font-size: ${Math.min(16,c.FontSize)}px; }\n`;
fs.writeFileSync(file,css);
console.log(`Layout checked: (${c.Left}, ${c.Top}), width ${c.Width}, maximum bottom ${c.Top+height}`);
