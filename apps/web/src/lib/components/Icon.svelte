<script lang="ts" module>
	import type { Component } from 'svelte';
	import Landmark from '@lucide/svelte/icons/landmark';
	import PiggyBank from '@lucide/svelte/icons/piggy-bank';
	import Banknote from '@lucide/svelte/icons/banknote';
	import Wallet from '@lucide/svelte/icons/wallet';
	import CreditCard from '@lucide/svelte/icons/credit-card';
	import TrendingUp from '@lucide/svelte/icons/trending-up';
	import HandCoins from '@lucide/svelte/icons/hand-coins';
	import Coins from '@lucide/svelte/icons/coins';
	import House from '@lucide/svelte/icons/house';
	import Car from '@lucide/svelte/icons/car';
	import Zap from '@lucide/svelte/icons/zap';
	import Shield from '@lucide/svelte/icons/shield';
	import ShoppingCart from '@lucide/svelte/icons/shopping-cart';
	import Repeat from '@lucide/svelte/icons/repeat';
	import Heart from '@lucide/svelte/icons/heart';
	import HeartHandshake from '@lucide/svelte/icons/heart-handshake';
	import Baby from '@lucide/svelte/icons/baby';
	import PawPrint from '@lucide/svelte/icons/paw-print';
	import PartyPopper from '@lucide/svelte/icons/party-popper';
	import Stethoscope from '@lucide/svelte/icons/stethoscope';
	import Ellipsis from '@lucide/svelte/icons/ellipsis';
	import Receipt from '@lucide/svelte/icons/receipt';
	import CalendarDays from '@lucide/svelte/icons/calendar-days';
	import ChartPie from '@lucide/svelte/icons/chart-pie';
	import Settings from '@lucide/svelte/icons/settings';
	import Users from '@lucide/svelte/icons/users';
	import Plus from '@lucide/svelte/icons/plus';
	import Copy from '@lucide/svelte/icons/copy';
	import Check from '@lucide/svelte/icons/check';
	import CircleCheck from '@lucide/svelte/icons/circle-check';
	import Camera from '@lucide/svelte/icons/camera';
	import CameraOff from '@lucide/svelte/icons/camera-off';
	import QrCode from '@lucide/svelte/icons/qr-code';
	import Keyboard from '@lucide/svelte/icons/keyboard';
	import LogOut from '@lucide/svelte/icons/log-out';
	import Sun from '@lucide/svelte/icons/sun';
	import Moon from '@lucide/svelte/icons/moon';
	import Monitor from '@lucide/svelte/icons/monitor';
	import Smartphone from '@lucide/svelte/icons/smartphone';
	import Share from '@lucide/svelte/icons/share';
	import Download from '@lucide/svelte/icons/download';
	import ChevronLeft from '@lucide/svelte/icons/chevron-left';
	import ChevronRight from '@lucide/svelte/icons/chevron-right';
	import ChevronDown from '@lucide/svelte/icons/chevron-down';
	import X from '@lucide/svelte/icons/x';
	import Pencil from '@lucide/svelte/icons/pencil';
	import Trash from '@lucide/svelte/icons/trash';
	import RefreshCw from '@lucide/svelte/icons/refresh-cw';
	import Link from '@lucide/svelte/icons/link';
	import WifiOff from '@lucide/svelte/icons/wifi-off';
	import CircleAlert from '@lucide/svelte/icons/circle-alert';
	import TriangleAlert from '@lucide/svelte/icons/triangle-alert';
	import Info from '@lucide/svelte/icons/info';
	import Sparkles from '@lucide/svelte/icons/sparkles';
	import CircleDollarSign from '@lucide/svelte/icons/circle-dollar-sign';
	import ExternalLink from '@lucide/svelte/icons/external-link';
	import Film from '@lucide/svelte/icons/film';
	import Utensils from '@lucide/svelte/icons/utensils';
	import Fuel from '@lucide/svelte/icons/fuel';
	import Lightbulb from '@lucide/svelte/icons/lightbulb';
	import Droplet from '@lucide/svelte/icons/droplet';
	import Gift from '@lucide/svelte/icons/gift';
	import Dumbbell from '@lucide/svelte/icons/dumbbell';
	import Phone from '@lucide/svelte/icons/phone';
	import Wifi from '@lucide/svelte/icons/wifi';
	import Tv from '@lucide/svelte/icons/tv';
	import Gamepad from '@lucide/svelte/icons/gamepad-2';
	import Plane from '@lucide/svelte/icons/plane';
	import GraduationCap from '@lucide/svelte/icons/graduation-cap';
	import Dog from '@lucide/svelte/icons/dog';
	import Eye from '@lucide/svelte/icons/eye';
	import EyeOff from '@lucide/svelte/icons/eye-off';
	import Bell from '@lucide/svelte/icons/bell';
	import Pencilline from '@lucide/svelte/icons/pencil-line';
	import Bot from '@lucide/svelte/icons/bot';
	import ListChecks from '@lucide/svelte/icons/list-checks';
	import Target from '@lucide/svelte/icons/target';
	import BellRing from '@lucide/svelte/icons/bell-ring';
	import KeyRound from '@lucide/svelte/icons/key-round';
	import Fingerprint from '@lucide/svelte/icons/fingerprint';
	import Printer from '@lucide/svelte/icons/printer';
	import ArrowUp from '@lucide/svelte/icons/arrow-up';
	import ArrowDown from '@lucide/svelte/icons/arrow-down';
	import ChevronUp from '@lucide/svelte/icons/chevron-up';
	import Activity from '@lucide/svelte/icons/activity';
	import CalendarClock from '@lucide/svelte/icons/calendar-clock';
	import SlidersHorizontal from '@lucide/svelte/icons/sliders-horizontal';
	import Undo2 from '@lucide/svelte/icons/undo-2';
	import ShoppingBag from '@lucide/svelte/icons/shopping-bag';
	import FileText from '@lucide/svelte/icons/file-text';
	import Globe from '@lucide/svelte/icons/globe';
	import CircleMinus from '@lucide/svelte/icons/circle-minus';
	import Send from '@lucide/svelte/icons/send';
	import Inbox from '@lucide/svelte/icons/inbox';
	import Upload from '@lucide/svelte/icons/upload';
	import ArrowLeftRight from '@lucide/svelte/icons/arrow-left-right';
	import Tag from '@lucide/svelte/icons/tag';
	import Pause from '@lucide/svelte/icons/pause';
	import Play from '@lucide/svelte/icons/play';
	import Unlink from '@lucide/svelte/icons/unlink';
	import EllipsisVertical from '@lucide/svelte/icons/ellipsis-vertical';
	import FileSpreadsheet from '@lucide/svelte/icons/file-spreadsheet';
	import CheckCheck from '@lucide/svelte/icons/check-check';
	import CalendarCheck from '@lucide/svelte/icons/calendar-check';
	import CalendarRange from '@lucide/svelte/icons/calendar-range';
	import Search from '@lucide/svelte/icons/search';
	import ArrowRight from '@lucide/svelte/icons/arrow-right';
	import Clock from '@lucide/svelte/icons/clock';
	import ListPlus from '@lucide/svelte/icons/list-plus';

	// eslint-disable-next-line @typescript-eslint/no-explicit-any
	const ICONS: Record<string, Component<any>> = {
		landmark: Landmark,
		'piggy-bank': PiggyBank,
		banknote: Banknote,
		wallet: Wallet,
		'credit-card': CreditCard,
		'trending-up': TrendingUp,
		'hand-coins': HandCoins,
		coins: Coins,
		house: House,
		home: House,
		car: Car,
		zap: Zap,
		shield: Shield,
		'shopping-cart': ShoppingCart,
		repeat: Repeat,
		heart: Heart,
		'heart-handshake': HeartHandshake,
		baby: Baby,
		'paw-print': PawPrint,
		'party-popper': PartyPopper,
		stethoscope: Stethoscope,
		ellipsis: Ellipsis,
		'more-horizontal': Ellipsis,
		receipt: Receipt,
		'calendar-days': CalendarDays,
		calendar: CalendarDays,
		'chart-pie': ChartPie,
		'pie-chart': ChartPie,
		settings: Settings,
		users: Users,
		plus: Plus,
		copy: Copy,
		check: Check,
		'circle-check': CircleCheck,
		camera: Camera,
		'camera-off': CameraOff,
		'qr-code': QrCode,
		keyboard: Keyboard,
		'log-out': LogOut,
		sun: Sun,
		moon: Moon,
		monitor: Monitor,
		smartphone: Smartphone,
		share: Share,
		download: Download,
		'chevron-left': ChevronLeft,
		'chevron-right': ChevronRight,
		'chevron-down': ChevronDown,
		x: X,
		pencil: Pencil,
		'pencil-line': Pencilline,
		trash: Trash,
		'refresh-cw': RefreshCw,
		link: Link,
		'wifi-off': WifiOff,
		'circle-alert': CircleAlert,
		'triangle-alert': TriangleAlert,
		info: Info,
		sparkles: Sparkles,
		'circle-dollar-sign': CircleDollarSign,
		'external-link': ExternalLink,
		film: Film,
		utensils: Utensils,
		fuel: Fuel,
		lightbulb: Lightbulb,
		droplet: Droplet,
		gift: Gift,
		dumbbell: Dumbbell,
		phone: Phone,
		wifi: Wifi,
		tv: Tv,
		'gamepad-2': Gamepad,
		plane: Plane,
		'graduation-cap': GraduationCap,
		dog: Dog,
		eye: Eye,
		'eye-off': EyeOff,
		bell: Bell,
		bot: Bot,
		'list-checks': ListChecks,
		target: Target,
		'bell-ring': BellRing,
		'key-round': KeyRound,
		fingerprint: Fingerprint,
		printer: Printer,
		'arrow-up': ArrowUp,
		'arrow-down': ArrowDown,
		'chevron-up': ChevronUp,
		activity: Activity,
		'calendar-clock': CalendarClock,
		'sliders-horizontal': SlidersHorizontal,
		'undo-2': Undo2,
		'shopping-bag': ShoppingBag,
		'file-text': FileText,
		globe: Globe,
		'circle-minus': CircleMinus,
		send: Send,
		inbox: Inbox,
		upload: Upload,
		'arrow-left-right': ArrowLeftRight,
		tag: Tag,
		pause: Pause,
		play: Play,
		unlink: Unlink,
		'ellipsis-vertical': EllipsisVertical,
		'file-spreadsheet': FileSpreadsheet,
		'check-check': CheckCheck,
		'calendar-check': CalendarCheck,
		'calendar-range': CalendarRange,
		search: Search,
		'arrow-right': ArrowRight,
		clock: Clock,
		'list-plus': ListPlus
	};

	export const ICON_NAMES = Object.keys(ICONS);
	export function hasIcon(name: string): boolean {
		return name in ICONS;
	}
</script>

<script lang="ts">
	let {
		name,
		size = 20,
		strokeWidth = 2,
		class: klass = '',
		label
	}: {
		/** lucide icon name (kebab-case); unknown names fall back to a coin */
		name: string;
		size?: number;
		strokeWidth?: number;
		class?: string;
		/** accessible label; omit for decorative icons */
		label?: string;
	} = $props();

	const Cmp = $derived(ICONS[name] ?? CircleDollarSign);
</script>

<Cmp
	{size}
	{strokeWidth}
	class={klass}
	aria-hidden={label ? undefined : 'true'}
	aria-label={label}
	role={label ? 'img' : undefined}
/>
