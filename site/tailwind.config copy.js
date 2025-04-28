import { fontFamily } from "tailwindcss/defaultTheme";

/** @type {import('tailwindcss').Config} */
const config = {
	darkMode: ["class"],
	content: ["./src/**/*.{html,js,svelte,ts}"],
	safelist: ["dark"],
	theme: {
		container: {
			center: true,
			padding: "2rem",
			screens: {
				"2xl": "1400px",
                "3xl": "1640px"
			}
		},
		extend: {
            screens: {
                "2xl": "1400px",
                "3xl": "1640px"
            },
			colors: {
				border: "oklch(var(--border) / <alpha-value>)",
				input: "oklch(var(--input) / <alpha-value>)",
				ring: "oklch(var(--ring) / <alpha-value>)",
				background: "hsl(var(--background) / <alpha-value>)",
                "background-article": "oklch(var(--background-article) / <alpha-value>)",
				foreground: "hsl(var(--foreground) / <alpha-value>)",
				primary: {
					DEFAULT: "oklch(var(--primary) / <alpha-value>)",
					foreground: "oklch(var(--primary-foreground) / <alpha-value>)"
				},
				secondary: {
					DEFAULT: "oklch(var(--secondary) / <alpha-value>)",
					foreground: "oklch(var(--secondary-foreground) / <alpha-value>)"
				},
				destructive: {
					DEFAULT: "oklch(var(--destructive) / <alpha-value>)",
					foreground: "oklch(var(--destructive-foreground) / <alpha-value>)"
				},
				muted: {
					DEFAULT: "oklch(var(--muted) / <alpha-value>)",
					foreground: "oklch(var(--muted-foreground) / <alpha-value>)"
				},
				accent: {
					DEFAULT: "oklch(var(--accent) / <alpha-value>)",
					foreground: "oklch(var(--accent-foreground) / <alpha-value>)"
				},
				popover: {
					DEFAULT: "oklch(var(--popover) / <alpha-value>)",
					foreground: "oklch(var(--popover-foreground) / <alpha-value>)"
				},
				card: {
					DEFAULT: "oklch(var(--card) / <alpha-value>)",
					foreground: "oklch(var(--card-foreground) / <alpha-value>)"
				},
				sidebar: {
					DEFAULT: "hsl(var(--sidebar-background) / <alpha-value>)",
					foreground: "hsl(var(--sidebar-foreground) / <alpha-value>)"
				},
				"sidebar-primary": {
					DEFAULT: "hsl(var(--sidebar-primary) / <alpha-value>)",
					foreground: "hsl(var(--sidebar-primary-foreground) / <alpha-value>)"
				},
				"sidebar-accent": {
					DEFAULT: "hsl(var(--sidebar-accent) / <alpha-value>)",
					foreground: "hsl(var(--sidebar-accent-foreground) / <alpha-value>)"
				},
				"sidebar-border": {
					DEFAULT: "hsl(var(--sidebar-border) / <alpha-value>)",
				},
				"sidebar-ring": {
					DEFAULT: "hsl(var(--sidebar-ring) / <alpha-value>)"
				}
			},
			borderRadius: {
				lg: "var(--radius)",
				md: "calc(var(--radius) - 2px)",
				sm: "calc(var(--radius) - 4px)"
			},
			fontFamily: {
				sans: ["Public Sans", ...fontFamily.sans],
				mono: ["Cascadia Code", ...fontFamily.mono],
				display: ["Space Grotesk", ...fontFamily.sans]
			},
			backgroundImage: {
				"gscode": "url('/images/gscode.png')",
				"gscodeLight": "url('/images/gscode-light.png')",
			},
		}
	},
    plugins: [
        require('@tailwindcss/typography')
    ]
};

export default config;
