import { defineEnvVars } from '@sveltejs/kit/env';

/**
 * Environment variables. Private ones are only available on the server via `$app/env/private`;
 * public ones are exposed to the browser via `$app/env/public`.
 */
export const variables = defineEnvVars({
	API_URL: {
		description: 'Base URL of the CouplesCash API (without the /v1 prefix).',
		schema: (value) => {
			const url = (value && value.trim()) || 'http://localhost:8080';
			return url.replace(/\/+$/, '');
		}
	},
	PUBLIC_APP_NAME: {
		public: true,
		description: 'Display name of the app.',
		schema: (value) => (value && value.trim()) || 'CouplesCash'
	}
});
