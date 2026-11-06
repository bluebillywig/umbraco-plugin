/**
 * The opening tags of one element in a source text, `<tag ...>`, found by scanning past `${...}` expressions (which may
 * contain `>` in arrow functions and nested template literals). A static check on Lit templates, no DOM needed.
 */
export function openingTags(text: string, tag: string): string[] {
  const found: string[] = [];
  const start = new RegExp(`<${tag}(?=[\\s>])`, "g");
  let m: RegExpExecArray | null;
  while ((m = start.exec(text))) {
    let depth = 0;
    let i = m.index + m[0].length;
    for (; i < text.length; i++) {
      const c = text[i];
      if (c === "$" && text[i + 1] === "{") {
        depth++;
        i++;
      } else if (depth > 0 && c === "{") depth++;
      else if (depth > 0 && c === "}") depth--;
      else if (depth === 0 && c === ">") break;
    }
    found.push(text.slice(m.index, i));
  }
  return found;
}
