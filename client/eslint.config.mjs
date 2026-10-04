import eslint from "@eslint/js";
import tseslint from "typescript-eslint";

export default tseslint.config(
    eslint.configs.recommended,
    ...tseslint.configs.recommended,
    {
        rules: {
            // The rule's default options, plus one exemption. A property whose name needs quotes is
            // a name something else chose - a VS Code setting key such as "format.maxBlankLines" -
            // and no casing it could be rewritten to would still be that key.
            "@typescript-eslint/naming-convention": [
                "warn",
                { selector: "default", format: ["camelCase"], leadingUnderscore: "allow", trailingUnderscore: "allow" },
                { selector: "import", format: ["camelCase", "PascalCase"] },
                { selector: "variable", format: ["camelCase", "UPPER_CASE"], leadingUnderscore: "allow", trailingUnderscore: "allow" },
                { selector: "typeLike", format: ["PascalCase"] },
                { selector: ["objectLiteralProperty", "typeProperty"], modifiers: ["requiresQuotes"], format: null },
            ],
            "curly": "warn",
            "eqeqeq": "warn",
            "no-throw-literal": "warn",
            "semi": "off",
        },
        ignores: ["out/**", "dist/**", "**/*.d.ts"],
    }
);
