import type { LayoutServerLoad } from './$types';

export const load: LayoutServerLoad = ({ locals }) => ({
	user: locals.user
		? { id: locals.user.id, email: locals.user.email, name: locals.user.name }
		: null,
	hasHousehold: locals.household !== null,
	theme: locals.theme
});
