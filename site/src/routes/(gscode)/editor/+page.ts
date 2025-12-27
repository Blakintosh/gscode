import type { PageLoad } from "./$types";

// No load function needed - the editor is created empty in +layout.ts
export const load: PageLoad = async () => {
    return {};
};
