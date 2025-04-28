<script lang="ts">
	import * as Alert from '$lib/components/ui/alert/index.js';
	import { Button } from '$components/ui/button';
	import type { Component } from 'svelte';

    type FlagAlertProps = {
        Icon: Component,
        title: string,
        description: string
    }

    let { Icon, title, description }: FlagAlertProps = $props();
</script>

<Alert.Root class={'[&:has(svg)]:pl-12'}>
    <Icon class="w-6 h-6" />
    <Alert.Title class="text-sm lg:text-base">{title}</Alert.Title>
    <Alert.Description class="flex flex-col gap-2 items-start text-xs lg:text-sm">
        {description}

        <!-- <Button
            variant="link"
            class="text-primary px-0 h-auto hidden lg:block"
            size={'sm'}
            href={'https://github.com/blakintosh/gscode-site'}>Report an issue</Button
        > -->
    </Alert.Description>
</Alert.Root>